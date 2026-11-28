param(
    [Parameter(Mandatory)]
    [int] $AppPid,
    [ValidateSet('local', 'sandbox')]
    [string] $Target = 'local',
    [string] $TestFilter,
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot "../../artifacts/omnibox-ui/$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
Assert-UiTestProcess $AppPid $Target
Assert-UiTestPath $ArtifactDirectory
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()

function Invoke-Ui([string[]] $Arguments, [long] $Window = $script:mainWindow) {
    $output = @(& winapp ui @Arguments -w $Window --on $Target 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)" }
    $output
}

function Get-Elements($Nodes) {
    foreach ($node in $Nodes) {
        $node
        if ($node.children) { Get-Elements $node.children }
    }
}

function Read-Ui {
    $json = Invoke-Ui @('inspect', '-d', '18', '--hide-offscreen', '--json')
    $json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Encoding utf8
    $inspection = $json | ConvertFrom-Json
    @(Get-Elements $inspection.windows[0].elements)
}

function Wait-State([scriptblock] $Check) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            $elements = @(Read-Ui)
            if (& $Check $elements) { return }
        }
        catch {
            if ($_ -notmatch 'stale_element') { throw }
        }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw 'The expected UI state did not appear within 15 seconds. See latest.json.'
}

function Expect-Location([string] $Address) {
    $breadcrumbAddress = if ($Address -eq 'Computer') { $Address } else { "Computer\$Address" }
    Wait-State {
        param($elements)
        $crumbs = @($elements | Where-Object { $_.automationId -match '^Breadcrumb\d+$' })
        ($crumbs.name -join '\') -eq $breadcrumbAddress -and $elements.automationId -notcontains 'OmniboxInput'
    }
}

function Edit-Address {
    Invoke-Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
    Invoke-Ui @('wait-for', 'OmniboxInput', '--type', 'Edit', '-t', '5000') | Out-Null
}

function Navigate([string] $Address, [string] $Expected) {
    Edit-Address
    Invoke-Ui @('set-value', 'OmniboxInput', $Address) | Out-Null
    Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
    Expect-Location $Expected
    Expect-ListKeyboardNavigation
}

function Add-CurrentFavorite([string] $Name) {
    Invoke-Ui @('click', 'FavoriteButton') | Out-Null
    Invoke-Ui @('wait-for', 'NameInput', '--type', 'Edit', '-t', '5000') | Out-Null
    Invoke-Ui @('set-value', 'NameInput', $Name) | Out-Null
    Invoke-Ui @('invoke', 'PrimaryButton', '--action', 'invoke') | Out-Null
    Wait-State { param($elements) $elements.name -contains 'Remove favorite' }
}

function Expect-ListKeyboardNavigation {
    Wait-State {
        param($elements)
        $list = $elements | Where-Object { $_.automationId -eq 'EntriesList' }
        $rows = @(Get-Elements $list.children | Where-Object { $_.type -eq 'ListItem' })
        $focused = (Invoke-Ui @('get-focused', '--json') | ConvertFrom-Json).element
        $focused.selector -eq 'EntriesList' -or $rows.selector -contains $focused.selector
    }
    Invoke-Ui @('send-keys', 'Home Down', '--via', 'send-input') | Out-Null
    $focused = (Invoke-Ui @('get-focused', '--json') | ConvertFrom-Json).element
    $list = Invoke-Ui @('inspect', 'EntriesList', '-d', '4', '--hide-offscreen', '--json') | ConvertFrom-Json
    $rows = @(Get-Elements $list.windows[0].elements | Where-Object { $_.type -eq 'ListItem' })
    $expected = $rows | Select-Object -Index ([Math]::Min(1, $rows.Count - 1))
    if (-not $expected -or $focused.selector -ne $expected.selector) {
        throw 'Home and Down after committing an address did not focus the expected destination list row.'
    }
    if ($rows.Count -gt 1) { Expect-SuggestionSelection $expected.selector }
}

function Enter-Scope([string] $Prefix, [string] $Confirmation, [string] $Label, [string] $Placeholder) {
    Edit-Address
    Invoke-Ui @('send-keys', $Prefix, '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
    if ($Confirmation) {
        Invoke-Ui @('send-keys', $Confirmation, '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
    }
    Expect-Scope $Label $Placeholder
}

function Expect-Scope([string] $Label, [string] $Placeholder) {
    Wait-State {
        param($elements)
        $elements.name -contains "Clear $Label context" -and
            @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.name -eq $Placeholder -and -not $_.value }).Count -eq 1
    }
}

function Expect-KeyboardFocus([string] $Selector) {
    $focus = Invoke-Ui @('get-property', $Selector, '-p', 'HasKeyboardFocus', '--json') | ConvertFrom-Json
    if ($focus.properties.HasKeyboardFocus -ne 'True') { throw "Expected keyboard focus on $Selector." }
}

function Get-AddressTabTarget([string] $Key) {
    Invoke-Ui @('send-keys', $Key, '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
    $id = (Invoke-Ui @('get-focused', '--json') | ConvertFrom-Json).element.automationId
    if (-not $id -or $id -eq 'EditAddressButton') { throw "Could not find the $Key target from Edit address." }
    $id
}

function Expect-SuggestionSelection([string] $Selector, [bool] $Selected = $true) {
    $item = Invoke-Ui @('get-property', $Selector, '-p', 'IsSelected', '--json') | ConvertFrom-Json
    if ($item.properties.IsSelected -ne $Selected.ToString()) { throw "Expected $Selector selection to be $Selected." }
}

function Expect-SuggestionRows([string] $Draft, [string[]] $Names) {
    Wait-State {
        param($elements)
        $editor = $elements | Where-Object automationId -eq 'OmniboxInput'
        $list = $elements | Where-Object automationId -eq 'SuggestionsList'
        $rows = @(Get-Elements $list.children | Where-Object type -eq 'ListItem')
        $editor -and [string]$editor.value -ceq $Draft -and ($rows.name -join '|') -ceq ($Names -join '|')
    }
}

function Test-Omnibox([string] $Name, [scriptblock] $Check) {
    if ($TestFilter -and $Name -notmatch $TestFilter) { return }
    try {
        & $Check
        $results.Add(@{ name = $Name; status = 'PASS' })
        Write-Output "PASS: $Name"
    }
    catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" })
        throw
    }
}

try {
    $windows = @(& winapp ui list-windows -a $AppPid --json --on $Target | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Could not find the app window.' }
    $appWindows = @($windows | Where-Object { $_.className -eq 'WinUIDesktopWin32WindowClass' })
    if ($appWindows.Count -ne 1) { throw 'Expected one registry editor window in the selected target.' }
    $script:mainWindow = $appWindows[0].hwnd

    Test-Omnibox 'Clicking empty address space opens the editor beside the favorite button' {
        $elements = @(Read-Ui)
        $bar = $elements | Where-Object automationId -eq 'AddressBar'
        $edit = $elements | Where-Object automationId -eq 'EditAddressButton'
        $favorite = $elements | Where-Object automationId -eq 'FavoriteButton'
        if (-not $edit.isInvokable -or [Math]::Abs($edit.x - $bar.x) -gt 1 -or
            [Math]::Abs($edit.x + $edit.width - $favorite.x) -gt 1) {
            throw 'Expected the edit action to span the address bar up to the favorite button.'
        }
        $x = [int]($edit.x + $edit.width - 4)
        $y = [int]($edit.y + $edit.height / 2)
        Invoke-Ui @('click', '--at', "$x,$y") | Out-Null
        Invoke-Ui @('wait-for', 'OmniboxInput', '--type', 'Edit', '-t', '5000') | Out-Null
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-KeyboardFocus 'EditAddressButton'
    }
    Test-Omnibox 'Alt+D and one Down previews the first starting suggestion' {
        $hives = 'HKEY_CLASSES_ROOT', 'HKEY_LOCAL_MACHINE', 'HKEY_USERS', 'HKEY_CURRENT_CONFIG'
        Invoke-Ui @('send-keys', 'alt+d', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
        Expect-SuggestionRows 'HKEY_CURRENT_USER' $hives
        $list = Read-Ui | Where-Object automationId -eq 'SuggestionsList'
        $rows = @(Get-Elements $list.children | Where-Object type -eq 'ListItem' | Select-Object -First 2)
        if (@(Get-Elements $rows[0].children).name -notcontains 'HKCR') { throw 'The first hive did not show its HKCR alias.' }
        Expect-SuggestionSelection $rows[0].selector $false
        foreach ($row in $rows) {
            Invoke-Ui @('send-keys', 'Down', '--via', 'send-input') | Out-Null
            Expect-SuggestionSelection $row.selector
            Expect-SuggestionRows $row.name $hives
            Expect-KeyboardFocus 'OmniboxInput'
        }
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER'
    }
    Test-Omnibox 'Up and Down wrap across suggestion groups and preserve keyboard focus' {
        $hives = 'HKEY_CLASSES_ROOT', 'HKEY_CURRENT_USER', 'HKEY_CURRENT_CONFIG'
        Edit-Address
        Invoke-Ui @('send-keys', 'hKc', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-SuggestionRows 'hKc' $hives
        $list = Read-Ui | Where-Object automationId -eq 'SuggestionsList'
        $rows = @(Get-Elements $list.children | Where-Object type -eq 'ListItem')
        foreach ($step in @(
            @{ Key = 'Down'; Row = $rows[0] }
            @{ Key = 'Up'; Row = $rows[-1] }
            @{ Key = 'Down'; Row = $rows[0] }
        )) {
            Invoke-Ui @('send-keys', $step.Key, '--via', 'send-input') | Out-Null
            Expect-SuggestionRows $step.Row.name $hives
            Expect-SuggestionSelection $step.Row.selector
            Expect-KeyboardFocus 'OmniboxInput'
        }
        Invoke-Ui @('send-keys', 'ctrl+z', '--via', 'send-input') | Out-Null
        Expect-SuggestionRows 'hKc' $hives
        Invoke-Ui @('send-keys', 'Up', '--via', 'send-input') | Out-Null
        Expect-SuggestionRows $rows[-1].name $hives
        Expect-SuggestionSelection $rows[-1].selector
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
    }
    Test-Omnibox 'Starting suggestions preserve Ctrl+L copy, Down/Enter navigation and Escape' {
        $currentUser = 'HKEY_CURRENT_USER'
        $otherHives = 'HKEY_CLASSES_ROOT', 'HKEY_LOCAL_MACHINE', 'HKEY_USERS', 'HKEY_CURRENT_CONFIG'
        Edit-Address
        Invoke-Ui @('send-keys', 'clipboard sentinel', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Invoke-Ui @('send-keys', 'ctrl+a ctrl+c Escape', '--via', 'send-input') | Out-Null
        Expect-Location $currentUser

        Invoke-Ui @('send-keys', 'ctrl+l', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
        Expect-SuggestionRows $currentUser $otherHives
        $list = Read-Ui | Where-Object automationId -eq 'SuggestionsList'
        foreach ($row in (Get-Elements $list.children | Where-Object type -eq 'ListItem')) {
            Expect-SuggestionSelection $row.selector $false
        }
        Invoke-Ui @('send-keys', 'ctrl+c Backspace', '--via', 'send-input') | Out-Null
        Expect-SuggestionRows '' $otherHives
        Invoke-Ui @('send-keys', 'ctrl+v', '--via', 'send-input') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -ceq $currentUser }).Count -eq 1 }
        $clear = Read-Ui | Where-Object automationId -eq 'ClearInputButton' | Select-Object -First 1
        Invoke-Ui @('invoke', $clear.selector, '--action', 'invoke') | Out-Null
        Expect-SuggestionRows '' $otherHives
        Invoke-Ui @('send-keys', 'Down', '--via', 'send-input') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -ceq $otherHives[0] }).Count -eq 1 }
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-Location $currentUser
        Expect-KeyboardFocus 'EditAddressButton'

        Navigate 'HKCU\Software' 'HKEY_CURRENT_USER\Software'
        Add-CurrentFavorite 'Keyboard favorite'
        Invoke-Ui @('invoke', 'Back', '--action', 'invoke') | Out-Null
        Expect-Location $currentUser
        Invoke-Ui @('send-keys', 'ctrl+l', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
        $bookmarkedVisit = @('Keyboard favorite') + $otherHives
        Expect-SuggestionRows $currentUser $bookmarkedVisit
        $list = Read-Ui | Where-Object automationId -eq 'SuggestionsList'
        $headers = Get-Elements $list.children | Where-Object { $_.name -in 'Suggestions', 'Hives' } | Select-Object -ExpandProperty name -Unique
        if (($headers -join '|') -cne 'Suggestions|Hives') { throw 'Expected the Suggestions heading above the remaining hives.' }
        $first = Get-Elements $list.children | Where-Object type -eq 'ListItem' | Select-Object -First 1
        if (-not (@(Get-Elements $first.children).name -like '*HKEY_CURRENT_USER\Software*')) {
            throw 'The bookmarked visit did not show its full destination beside the saved name.'
        }
        Invoke-Ui @('send-keys', 'Down', '--via', 'send-input') | Out-Null
        Expect-SuggestionRows 'HKEY_CURRENT_USER\Software' $bookmarkedVisit
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-Location $currentUser
        Navigate 'HKLM' 'HKEY_LOCAL_MACHINE'
        $expected = $currentUser, 'Keyboard favorite', 'HKEY_CLASSES_ROOT', 'HKEY_USERS', 'HKEY_CURRENT_CONFIG'
        Invoke-Ui @('send-keys', 'ctrl+l', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
        Expect-SuggestionRows 'HKEY_LOCAL_MACHINE' $expected
        Invoke-Ui @('screenshot', 'SuggestionsList', '--output', (Join-Path $ArtifactDirectory 'starting-groups.png')) | Out-Null
        Invoke-Ui @('send-keys', 'Backspace', '--via', 'send-input') | Out-Null
        Expect-SuggestionRows '' $expected
        Invoke-Ui @('send-keys', 'Down Enter', '--via', 'send-input') | Out-Null
        Expect-Location $currentUser
        Expect-ListKeyboardNavigation

        Navigate 'HKCU\Software' 'HKEY_CURRENT_USER\Software'
        Invoke-Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
        Wait-State { param($elements) $elements.name -contains 'Add favorite' }
        Invoke-Ui @('invoke', 'Back', '--action', 'invoke') | Out-Null
        Expect-Location $currentUser
    }

    Test-Omnibox 'Typed aliases become clickable breadcrumbs and focus the list' {
        Navigate 'HKCU\Software' 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'Ancestor navigation updates the current location' {
        $ancestor = Read-Ui | Where-Object { $_.automationId -eq 'Breadcrumb1' -and $_.name -eq 'HKEY_CURRENT_USER' }
        Invoke-Ui @('click', $ancestor.selector) | Out-Null
        Expect-Location 'HKEY_CURRENT_USER'
    }
    Test-Omnibox 'Back and forward update the breadcrumbs' {
        Invoke-Ui @('invoke', 'Back', '--action', 'invoke') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
        Invoke-Ui @('invoke', 'Forward', '--action', 'invoke') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER'
    }
    Test-Omnibox 'Ctrl+L and Alt+D select the confirmed address' {
        foreach ($shortcut in @('ctrl+l', 'alt+d')) {
            Invoke-Ui @('send-keys', $shortcut, '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
            Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' -and $_.value -eq 'HKEY_CURRENT_USER' }).Count -eq 1 }
            Invoke-Ui @('send-keys', 'HKCC', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
            Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' -and $_.value -eq 'HKCC' }).Count -eq 1 }
            Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
            Expect-Location 'HKEY_CURRENT_USER'
        }
    }
    Test-Omnibox 'Alt+D follows toolbar Tab order when suggestions are closed' {
        foreach ($key in @('Tab', 'shift+Tab')) {
            $expected = Get-AddressTabTarget $key

            Invoke-Ui @('send-keys', 'alt+d', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
            Invoke-Ui @('send-keys', 'NoSuchOmniboxDestination', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
            Wait-State {
                param($elements)
                @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -eq 'NoSuchOmniboxDestination' }).Count -eq 1 -and
                    $elements.automationId -notcontains 'OmniboxSuggestionFooter'
            }
            Expect-KeyboardFocus 'OmniboxInput'
            Invoke-Ui @('send-keys', $key, '--via', 'send-input') | Out-Null
            $actual = (Invoke-Ui @('get-focused', '--json') | ConvertFrom-Json).element.automationId
            if ($actual -ne $expected) { throw "$key from the omnibox focused '$actual'. Expected '$expected'." }
            Expect-Location 'HKEY_CURRENT_USER'
        }
    }

    foreach ($key in 'Tab', 'Shift+Tab') {
        Test-Omnibox "$key follows toolbar focus when a pending lookup returns no matches" {
            $expected = Get-AddressTabTarget $key
            Edit-Address
            Invoke-Ui @('send-keys', 'hkcu', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
            Expect-SuggestionRows 'hkcu' @('HKEY_CURRENT_USER')

            $missing = "__Omnibox_$([guid]::NewGuid().ToString('N'))"
            Invoke-Ui @('send-keys', "text=\\$missing $key", '--via', 'send-input') | Out-Null
            Wait-State { param($elements) $elements.automationId -notcontains 'OmniboxSuggestionFooter' }
            Expect-KeyboardFocus $expected
        }
    }

    Test-Omnibox 'Malformed paths retain the draft and keyboard focus' {
        Edit-Address
        Invoke-Ui @('set-value', 'OmniboxInput', 'InvalidHive\Software') | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.automationId -like 'Notification_*' }).Count -gt 0 }
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER'
    }
    Test-Omnibox 'Invalid paths retain the draft, keyboard focus and current key' {
        $missing = 'HKCU\Software\__Omnibox_Test_' + [guid]::NewGuid().ToString('N')
        Edit-Address
        Invoke-Ui @('set-value', 'OmniboxInput', $missing) | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.automationId -like 'Notification_*' }).Count -gt 0 -and
                @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' -and $_.value -eq $missing }).Count -eq 1
        }
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'rejected-address.png')) | Out-Null
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER'
    }
    Test-Omnibox 'Subkey suggestions can be chosen with the keyboard' {
        Edit-Address
        Invoke-Ui @('send-keys', 'HKCU\Softw', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-SuggestionRows 'HKCU\Softw' @('Software')
        foreach ($key in @('Tab', 'Down', 'Up', 'Down')) {
            Invoke-Ui @('send-keys', $key, '--via', 'send-input') | Out-Null
            Wait-State {
                param($elements)
                @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -eq 'HKEY_CURRENT_USER\Software' }).Count -eq 1
            }
            Expect-KeyboardFocus 'OmniboxInput'
            if ($key -eq 'Tab') {
                Invoke-Ui @('send-keys', 'shift+Tab', '--via', 'send-input') | Out-Null
                Wait-State {
                    param($elements)
                    @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -eq 'HKCU\Softw' }).Count -eq 1
                }
            }
        }
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
        Expect-ListKeyboardNavigation
        if (@((Read-Ui) | Where-Object { $_.automationId -like 'Notification_*' }).Count -gt 0) { throw 'Successful navigation did not clear the old error.' }
    }
    Test-Omnibox 'A fast Tab completes the latest subkey draft and further typing cancels it' {
        foreach ($case in @(
            @{ Keys = 'text=\\soft Tab'; Expected = 'HKEY_CURRENT_USER\Software'; Undo = $true }
            @{ Keys = 'text=\\soft Tab w'; Expected = 'hkcu\softw' }
            @{ Keys = 'text=\\soft Tab w Backspace'; Expected = 'hkcu\soft' }
        )) {
            Edit-Address
            Invoke-Ui @('send-keys', 'hkcu', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
            Expect-SuggestionRows 'hkcu' @('HKEY_CURRENT_USER')

            Invoke-Ui @('send-keys', $case.Keys, '--via', 'send-input') | Out-Null
            Expect-SuggestionRows $case.Expected @('Software')
            Expect-KeyboardFocus 'OmniboxInput'
            if ($case.Undo) {
                Invoke-Ui @('send-keys', 'ctrl+z', '--via', 'send-input') | Out-Null
                Expect-SuggestionRows 'hkcu\soft' @('Software')
            }

            Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        }
    }
    Test-Omnibox 'Enter submits the full typed address while the hive row is stale' {
        Edit-Address
        Invoke-Ui @('send-keys', 'hkcu', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-SuggestionRows 'hkcu' @('HKEY_CURRENT_USER')
        Invoke-Ui @('send-keys', 'text=\\Software Enter', '--via', 'send-input') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'Escape prevents delayed suggestions from reopening the editor' {
        Edit-Address
        Invoke-Ui @('send-keys', 'HKCR\', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Start-Sleep -Milliseconds 500
        Expect-Location 'HKEY_CURRENT_USER\Software'
        $popups = @(& winapp ui list-windows -a $AppPid --json --on $Target | ConvertFrom-Json | Where-Object { $_.label -eq 'popup' })
        if ($LASTEXITCODE -ne 0 -or $popups.Count -gt 0) { throw 'A suggestion popup remained after cancelling.' }
    }
    Test-Omnibox 'Ctrl+Z restores the typed address after one or more Tab previews' {
        function Expect-Draft([string] $Text) {
            Wait-State {
                param($elements)
                @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -ceq $Text }).Count -eq 1
            }
        }

        $hiveMatches = 'HKEY_CLASSES_ROOT', 'HKEY_CURRENT_USER', 'HKEY_CURRENT_CONFIG'
        foreach ($case in @(
            @{ Draft = 'HKCU\Softw'; Rows = @('Software'); Previews = @('HKEY_CURRENT_USER\Software') }
            @{ Draft = 'hKc'; Rows = $hiveMatches; Previews = $hiveMatches }
        )) {
            Edit-Address
            Invoke-Ui @('send-keys', $case.Draft, '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
            Expect-SuggestionRows $case.Draft $case.Rows
            foreach ($preview in $case.Previews) {
                Invoke-Ui @('send-keys', 'Tab', '--via', 'send-input') | Out-Null
                Expect-Draft $preview
            }
            $list = Read-Ui | Where-Object automationId -eq 'SuggestionsList'
            $rows = @(Get-Elements $list.children | Where-Object type -eq 'ListItem')
            $row = $rows[$case.Previews.Count - 1]
            Expect-SuggestionSelection $row.selector

            Invoke-Ui @('send-keys', 'ctrl+z', '--via', 'send-input') | Out-Null
            Expect-Draft $case.Draft
            Expect-SuggestionSelection $row.selector $false
            Expect-KeyboardFocus 'OmniboxInput'
            Invoke-Ui @('send-keys', 'a', '--verbatim', '--via', 'send-input') | Out-Null
            Expect-Draft ($case.Draft + 'a')
            Invoke-Ui @('send-keys', 'ctrl+z', '--via', 'send-input') | Out-Null
            Expect-Draft $case.Draft
            Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        }
    }
    Test-Omnibox 'Tab previews contexts while the textbox keeps focus' {
        Edit-Address
        Invoke-Ui @('send-keys', '@', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            $contexts = @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '@*' })
            ($contexts.name -join ' ') -eq '@favorites @history @tabs @search @commands'
        }
        $contexts = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '@*' })
        foreach ($context in $contexts) {
            Invoke-Ui @('send-keys', 'Tab', '--via', 'send-input') | Out-Null
            Expect-SuggestionSelection $context.selector
            Expect-KeyboardFocus 'OmniboxInput'
            Wait-State {
                param($elements)
                @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -eq $context.name }).Count -eq 1
            }
        }
        Invoke-Ui @('send-keys', 'Tab', '--via', 'send-input') | Out-Null
        Expect-SuggestionSelection $contexts[-1].selector $false
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -eq '@' }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'shift+Tab', '--via', 'send-input') | Out-Null
        Expect-SuggestionSelection $contexts[-1].selector
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -eq $contexts[-1].name }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Tab Tab Tab shift+Tab', '--via', 'send-input') | Out-Null
        Expect-SuggestionSelection $contexts[0].selector
        Expect-KeyboardFocus 'OmniboxInput'
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -eq '@favorites' }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input') | Out-Null
        Expect-Scope 'Favorites' 'Find favorites'
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'The Favorites alias opens immediately and Backspace clears it' {
        Enter-Scope '*' '' 'Favorites' 'Find favorites'
        Invoke-Ui @('send-keys', 'Backspace', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            $elements.automationId -notcontains 'OmniboxScopeToken' -and
                @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.name -eq 'Address' }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'Tab completes an unambiguous prefix and Enter opens a matching history entry' {
        Enter-Scope '@HiSt' 'Tab' 'History' "Search this tab's history"
        Invoke-Ui @('send-keys', 'HKEY_CURRENT_USER', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER'
        Navigate 'HKCU\Software' 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'The command alias opens immediately and the Tabs context switches back' {
        $tabs = @(Read-Ui | Where-Object type -eq 'TabItem').Count
        Enter-Scope '>' '' 'Commands' 'Type a command'
        Invoke-Ui @('send-keys', 'new tab', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State { param($elements) @(($elements | Where-Object type -eq 'TabItem')).Count -eq $tabs + 1 }
        Navigate 'HKCC' 'HKEY_CURRENT_CONFIG'
        Enter-Scope '@tabs' 'Space' 'Tabs' 'Search open tabs'
        Invoke-Ui @('send-keys', 'HKEY_CURRENT_USER\Software', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Invoke-Ui @('send-keys', 'Down Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'Ctrl+Shift+P opens the grouped palette and Down selects a command' {
        $tabs = @(Read-Ui | Where-Object type -eq 'TabItem').Count
        Invoke-Ui @('send-keys', 'ctrl+shift+p', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
        Wait-State {
            param($elements)
            $elements.name -contains 'Clear Commands context' -and $elements.automationId -contains 'OmniboxSuggestionFooter'
        }
        Invoke-Ui @('send-keys', 'registry search', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Search the registry' }).Count -eq 1
        }
        Invoke-Ui @('set-value', 'OmniboxInput', 'missing-command-xyz') | Out-Null
        Expect-SuggestionRows 'missing-command-xyz' @('No matching commands')
        Invoke-Ui @('send-keys', 'Up Down', '--via', 'send-input') | Out-Null
        $statusRow = Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'No matching commands' }
        Expect-SuggestionSelection $statusRow.selector $false
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'ctrl+a Backspace', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Invoke-Ui @('send-keys', 'Up Down Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object type -eq 'TabItem').Count -eq $tabs + 1 }
        Expect-Location 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'Tab selects Show more and Enter expands the group from the input' {
        Enter-Scope '>' '' 'Commands' 'Type a command'
        Invoke-Ui @('send-keys', 'open', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'OmniboxShowMore' }

        $rows = @(Read-Ui | Where-Object type -eq 'ListItem')
        $showMore = $rows | Where-Object { (Get-Elements $_.children).automationId -contains 'OmniboxShowMore' } | Select-Object -First 1
        if (-not $showMore) { throw 'Could not find the Show more row.' }
        foreach ($row in ($rows | Where-Object { $_.name -like 'Open *' -or $_.selector -eq $showMore.selector })) {
            Invoke-Ui @('send-keys', 'Tab', '--via', 'send-input') | Out-Null
            Expect-SuggestionSelection $row.selector
            if ($row.selector -eq $showMore.selector) { break }
        }
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input') | Out-Null
        Wait-State {
            param($elements)
            $elements.automationId -notcontains 'OmniboxShowMore' -and $elements.automationId -contains 'OmniboxSuggestionFooter'
        }
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'Computer is the single root and its menu navigates to a hive' {
        $elements = @(Read-Ui)
        if ($elements.automationId -contains 'RootButton' -or
            -not @($elements | Where-Object { $_.automationId -eq 'Breadcrumb0' -and $_.name -eq 'Computer' }).Count) {
            throw 'Expected Computer as the first breadcrumb without a duplicate root selector.'
        }
        Invoke-Ui @('send-keys', 'Space', '--via', 'send-input', '--target', 'BreadcrumbChildren0') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'MenuFilterBox' }
        Invoke-Ui @('send-keys', 'current_config', '--verbatim', '--via', 'send-input') | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.type -eq 'MenuItem' -and $_.name -eq 'HKEY_CURRENT_CONFIG' }).Count -eq 1
        }
        Invoke-Ui @('invoke', 'HKEY_CURRENT_CONFIG', '--type', 'MenuItem', '--action', 'invoke') | Out-Null
        Expect-Location 'HKEY_CURRENT_CONFIG'
    }
    Test-Omnibox 'A segment chevron filters its children before navigating' {
        Navigate 'HKCU' 'HKEY_CURRENT_USER'
        Invoke-Ui @('click', 'BreadcrumbChildren1') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'MenuFilterBox' }
        Expect-KeyboardFocus 'MenuFilterBox'
        Invoke-Ui @('send-keys', 'software', '--verbatim', '--via', 'send-input') | Out-Null
        Wait-State {
            param($elements)
            $list = $elements | Where-Object automationId -eq 'FilteredMenuItems'
            $rows = @(Get-Elements $list.children | Where-Object type -eq 'MenuItem')
            $rows.Count -gt 0 -and -not ($rows.name -notlike '*software*')
        }
        Expect-Location 'HKEY_CURRENT_USER'
        $list = Read-Ui | Where-Object automationId -eq 'FilteredMenuItems'
        $first = Get-Elements $list.children | Where-Object type -eq 'MenuItem' | Select-Object -First 1
        Invoke-Ui @('screenshot', 'FilteredMenuItems', '--output', (Join-Path $ArtifactDirectory 'children.png')) | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input') | Out-Null
        Expect-Location "HKEY_CURRENT_USER\$($first.name)"
    }
    Test-Omnibox 'Down opens a segment menu and Escape restores its focus' {
        Navigate 'HKCU\Software' 'HKEY_CURRENT_USER\Software'
        foreach ($button in 'BreadcrumbChildren1', 'Breadcrumb1') {
            Wait-State { param($elements) $elements.automationId -notcontains 'MenuFilterBox' }
            Invoke-Ui @('send-keys', 'Down', '--via', 'send-input', '--target', $button) | Out-Null
            Wait-State { param($elements) $elements.automationId -contains 'MenuFilterBox' }
            Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
            Expect-Location 'HKEY_CURRENT_USER\Software'
            Wait-State {
                param($elements)
                $focus = Invoke-Ui @('get-property', $button, '-p', 'HasKeyboardFocus', '--json') | ConvertFrom-Json
                $focus.properties.HasKeyboardFocus -eq 'True'
            }
        }
    }
    Test-Omnibox 'Large hive children support scrolling and keyboard navigation' {
        Navigate 'HKCR' 'HKEY_CLASSES_ROOT'
        Invoke-Ui @('send-keys', 'Down', '--via', 'send-input', '--target', 'Breadcrumb1') | Out-Null
        Wait-State {
            param($elements)
            $menu = $elements | Where-Object automationId -eq 'FilteredMenuItems'
            @(Get-Elements $menu.children | Where-Object type -eq 'MenuItem').Count -gt 0
        }
        $popup = & winapp ui list-windows -a $AppPid --json --on $Target | ConvertFrom-Json | Where-Object label -eq 'popup' | Select-Object -First 1
        Invoke-Ui @('send-keys', 'Down', '--via', 'send-input') | Out-Null
        Wait-State { (Invoke-Ui @('get-focused', '--json') $popup.hwnd | ConvertFrom-Json).element.type -eq 'MenuItem' }
        Invoke-Ui @('send-keys', 'End', '--via', 'send-input') | Out-Null
        Wait-State {
            $menu = Invoke-Ui @('inspect', 'FilteredMenuItems', '-d', '4', '--hide-offscreen', '--json') $popup.hwnd | ConvertFrom-Json
            $visible = @(Get-Elements $menu.windows[0].elements | Where-Object type -eq 'MenuItem')
            $focused = (Invoke-Ui @('get-focused', '--json') $popup.hwnd | ConvertFrom-Json).element
            $visible.Count -in 1..20 -and $focused.type -eq 'MenuItem' -and $focused.name -eq $visible[-1].name
        }
        $destination = 'HKEY_CLASSES_ROOT\' + (Invoke-Ui @('get-focused', '--json') $popup.hwnd | ConvertFrom-Json).element.name
        Invoke-Ui @('screenshot', 'FilteredMenuItems', '--output', (Join-Path $ArtifactDirectory 'large-hive.png')) | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input') | Out-Null
        Expect-Location $destination
    }
    Test-Omnibox 'Hiding the separate search box gives the omnibox its space and routes Ctrl+F to Search' {
        function Set-SeparateSearchBox([string] $Action) {
            Invoke-Ui @('send-keys', 'ctrl+,', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
            Invoke-Ui @('wait-for', 'ShowSearchBoxSwitch', '-t', '5000') | Out-Null
            Invoke-Ui @('invoke', 'ShowSearchBoxSwitch', '--action', $Action) | Out-Null
            Invoke-Ui @('send-keys', 'ctrl+w', '--via', 'send-input', '--target', 'ShowSearchBoxSwitch') | Out-Null
            Expect-Location 'HKEY_CURRENT_CONFIG'
        }

        Navigate 'HKCC' 'HKEY_CURRENT_CONFIG'
        Edit-Address
        $originalWidth = (Read-Ui | Where-Object automationId -eq 'OmniboxInput' | Select-Object -First 1).width
        if ($originalWidth -le 0) { throw 'Could not measure the original omnibox width.' }
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Set-SeparateSearchBox 'toggle-off'
        Invoke-Ui @('send-keys', 'ctrl+f', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
        Expect-Scope 'Search' 'Search here'
        Invoke-Ui @('send-keys', 'SearchDraft', '--verbatim', '--via', 'send-input') | Out-Null
        Wait-State {
            param($elements)
            $elements.automationId -notcontains 'SearchSplitter' -and
                $elements.automationId -notcontains 'AdvancedSearchButton' -and
                @($elements | Where-Object {
                    $_.automationId -eq 'OmniboxInput' -and $_.width -gt $originalWidth -and $_.value -eq 'SearchDraft'
                }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Set-SeparateSearchBox 'toggle-on'
        Wait-State {
            param($elements)
            $elements.automationId -contains 'SearchSplitter' -and $elements.automationId -contains 'AdvancedSearchButton'
        }
    }
    Test-Omnibox 'Advanced search is available before typing a query' {
        Navigate 'HKCC' 'HKEY_CURRENT_CONFIG'
        Enter-Scope '?' '' 'Search' 'Search here'
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Advanced search…' -and $_.isEnabled }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Tab Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            $elements.automationId -contains 'SearchScope' -and
                @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Find what' -and -not $_.value }).Count -eq 1
        }
        Invoke-Ui @('invoke', 'Cancel', '--action', 'invoke') | Out-Null
        Expect-Location 'HKEY_CURRENT_CONFIG'
    }
    Test-Omnibox 'Advanced search preserves whitespace in a pasted omnibox query' {
        Navigate 'HKCC' 'HKEY_CURRENT_CONFIG'
        $query = ' OmniboxAdvancedFixture '
        Edit-Address
        Invoke-Ui @('set-value', 'OmniboxInput', "?$query") | Out-Null
        Wait-State {
            param($elements)
            $elements.name -contains 'Clear Search context' -and
                @($elements | Where-Object {
                    $_.automationId -eq 'OmniboxInput' -and $_.value -eq $query
                }).Count -eq 1 -and
                @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Advanced search…' -and $_.isEnabled }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Down Down Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            $elements.automationId -contains 'SearchScope' -and
                @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Find what' -and $_.value -eq $query }).Count -eq 1
        }
        Invoke-Ui @('invoke', 'Cancel', '--action', 'invoke') | Out-Null
        Expect-Location 'HKEY_CURRENT_CONFIG'
        if ((Read-Ui).name -contains "Find: $query") { throw 'Cancelling advanced search opened results.' }
    }
    Test-Omnibox 'Quick search preserves whitespace after the explicit scope prefix' {
        Navigate 'HKCC' 'HKEY_CURRENT_CONFIG'
        $query = ' OmniboxQuickWhitespace '
        Edit-Address
        Invoke-Ui @('set-value', 'OmniboxInput', "@search $query") | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -eq $query }).Count -eq 1 -and
                @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Search this branch' }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -like 'Find: *OmniboxQuickWhitespace*' }).Count -eq 1 -and
                @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Search registry' -and $_.value -eq $query }).Count -eq 1
        }
    }
    Test-Omnibox 'Question mark starts the existing branch search from the omnibox' {
        Navigate 'HKCC' 'HKEY_CURRENT_CONFIG'
        Enter-Scope '?' '' 'Search' 'Search here'
        $query = 'OmniboxSearchFixture'
        Invoke-Ui @('send-keys', $query, '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Search this branch' -and $_.isEnabled }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            $elements.automationId -notcontains 'OmniboxInput' -and
                @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq "Find: $query" }).Count -eq 1 -and
                @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Search registry' -and $_.value -eq $query }).Count -eq 1
        }
    }
    Test-Omnibox 'The omnibox and separate search box share recent queries' {
        Invoke-Ui @('send-keys', 'ctrl+f', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
        Wait-State {
            param($elements)
            $recent = $elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'OmniboxSearchFixture' } | Select-Object -First 1
            $start = $elements | Where-Object automationId -eq 'QuickSearchButton' | Select-Object -First 1
            $advanced = $elements | Where-Object automationId -eq 'AdvancedSearchButton' | Select-Object -First 1
            $recent -and $start -and $advanced -and $recent.x -le $start.x -and
                ($recent.x + $recent.width) -ge ($advanced.x + $advanced.width)
        }
        $input = Read-Ui | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Search registry' } | Select-Object -First 1
        $query = 'ToolbarHistoryFixture'
        Invoke-Ui @('set-value', $input.selector, $query) | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', $input.selector) | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq "Find: $query" }).Count -eq 1
        }

        Enter-Scope '?' '' 'Search' 'Search here'
        Wait-State {
            param($elements)
            $queries = @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -in @($query, 'OmniboxSearchFixture') })
            ($queries.name -join ' ') -eq "$query OmniboxSearchFixture"
        }
        Invoke-Ui @('send-keys', 'Down Down Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq "Find: $query" }).Count -eq 2
        }
    }
    Test-Omnibox 'Committing Computer lets arrow keys navigate the hive list' {
        Navigate 'Computer' 'Computer'
        Invoke-Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'list-keyboard-focus.png')) | Out-Null
    }
    Test-Omnibox 'Committing the current address returns keyboard focus to the list' {
        Navigate 'Computer' 'Computer'
    }
    Test-Omnibox 'A bare favorite name distinguishes literal Enter from choosing an address' {
        Navigate 'Computer' 'Computer'
        Navigate 'HKCU\Software' 'HKEY_CURRENT_USER\Software'
        Add-CurrentFavorite 'Startup'
        Navigate 'HKLM' 'HKEY_LOCAL_MACHINE'

        $typeQuery = {
            Edit-Address
            Invoke-Ui @('send-keys', 'startup', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
            Wait-State {
                param($elements)
                $list = $elements | Where-Object automationId -eq 'SuggestionsList'
                $children = @(Get-Elements $list.children)
                $children.name -contains 'Suggestions' -and @($children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Startup*' }).Count -eq 1
            }
        }
        & $typeQuery
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State { param($elements) $elements.automationId -like 'Notification_*' -and
            @($elements | Where-Object { $_.automationId -eq 'OmniboxInput' -and $_.value -ceq 'startup' }).Count -eq 1 }
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-Location 'HKEY_LOCAL_MACHINE'

        & $typeQuery
        Invoke-Ui @('send-keys', 'Down Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
        Expect-ListKeyboardNavigation
        Invoke-Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
        Wait-State { param($elements) $elements.name -contains 'Add favorite' }
        Invoke-Ui @('invoke', 'Back', '--action', 'invoke') | Out-Null
        Expect-Location 'HKEY_LOCAL_MACHINE'
    }
    Test-Omnibox 'A populated address bar shows ten complete destinations across four groups' {
        $bookmarks = [ordered]@{
            'HKEY_CURRENT_USER\Control Panel\Desktop' = 'Desktop appearance'
            'HKEY_CURRENT_USER\Environment' = 'User environment'
        }
        foreach ($entry in $bookmarks.GetEnumerator()) {
            Navigate $entry.Key $entry.Key
            Add-CurrentFavorite $entry.Value
        }
        foreach ($path in @('HKEY_CURRENT_USER\Control Panel\Colors', 'HKEY_CURRENT_USER\Keyboard Layout',
                'HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control', 'HKEY_CURRENT_USER\Control Panel\Mouse',
                'HKEY_CURRENT_USER\Software\Microsoft', 'HKEY_CURRENT_USER\Software')) {
            Navigate $path $path
        }

        Edit-Address
        Wait-State {
            param($elements)
            $list = $elements | Where-Object automationId -eq 'SuggestionsList'
            $children = @(Get-Elements $list.children)
            $rows = @($children | Where-Object { $_.type -eq 'ListItem' -and $_.isEnabled })
            $visible = @($rows | Where-Object {
                $_.height -ge $rows[0].height - 1 -and $_.y -ge $list.y - 1 -and $_.y + $_.height -le $list.y + $list.height + 1
            })
            $visible.Count -ge 10 -and $children.name -contains 'Suggestions' -and $children.name -contains 'History' -and
                $children.name -contains 'Favorites' -and $children.name -contains 'Hives'
        }
        Invoke-Ui @('screenshot', 'SuggestionsList', '--output', (Join-Path $ArtifactDirectory 'populated-address-list.png')) | Out-Null
        foreach ($step in @(
            @{ Keys = ('Down ' * 12).Trim(); Label = 'HKEY_CURRENT_CONFIG'; Address = 'HKEY_CURRENT_CONFIG' }
            @{ Keys = 'Down'; Label = 'Microsoft'; Address = 'HKEY_CURRENT_USER\Software\Microsoft' }
            @{ Keys = 'Up'; Label = 'HKEY_CURRENT_CONFIG'; Address = 'HKEY_CURRENT_CONFIG' }
        )) {
            Invoke-Ui @('send-keys', $step.Keys, '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
            Wait-State {
                param($elements)
                $list = $elements | Where-Object automationId -eq 'SuggestionsList'
                $row = Get-Elements $list.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $step.Label }
                ($elements | Where-Object automationId -eq 'OmniboxInput').value -ceq $step.Address -and
                    $row -and $row.y -ge $list.y - 1 -and $row.y + $row.height -le $list.y + $list.height + 1
            }
        }
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        foreach ($path in $bookmarks.Keys) {
            Navigate $path $path
            Invoke-Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
            Wait-State { param($elements) $elements.name -contains 'Add favorite' }
        }
    }
    Test-Omnibox 'The address list opens above the textbox near the bottom of the work area' {
        . (Join-Path $PSScriptRoot 'UiTestWindow.ps1')
        Add-Type -AssemblyName System.Windows.Forms
        $work = [System.Windows.Forms.Screen]::FromHandle([IntPtr]$mainWindow).WorkingArea
        $window = Read-Ui | Where-Object { $_.type -eq 'Window' } | Select-Object -First 1
        $button = Read-Ui | Where-Object automationId -eq 'EditAddressButton'
        $targetY = $work.Bottom - 2 * $button.height - ($button.y - $window.y)
        try {
            if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, $window.x, $targetY, 0, 0, 0x15)) {
                throw 'Could not move the test window near the bottom of the screen.'
            }
            Wait-State { param($elements) ($elements | Where-Object automationId -eq 'EditAddressButton').y -ge $work.Bottom - 2 * $button.height - 1 }
            $anchor = Read-Ui | Where-Object automationId -eq 'EditAddressButton'
            Edit-Address
            Invoke-Ui @('set-value', 'OmniboxInput', 'HKLM\Software\') | Out-Null
            Wait-State {
                param($elements)
                $inputBox = $elements | Where-Object automationId -eq 'OmniboxInput'
                $list = $elements | Where-Object automationId -eq 'SuggestionsList'
                $list -and @(Get-Elements $list.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Classes' }).Count -eq 1 -and
                    $list.height -gt $work.Bottom - $anchor.y -and $list.y -ge $work.Top - 1 -and
                    $list.y + $list.height -le $inputBox.y + 1 -and [Math]::Abs($inputBox.y - $anchor.y) -le 1
            }
            Expect-KeyboardFocus 'OmniboxInput'
            Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        }
        finally { [UxTestWindow]::SetWindowPos($mainWindow, 0, $window.x, $window.y, 0, 0, 0x15) | Out-Null }
    }
    Test-Omnibox 'Favorite row actions reveal the bookmark and remove it without navigating' {
        $name = 'Omnibox action bookmark'
        $actions = "More actions for $name"
        $favoriteRow = { Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $name } }
        $hiveHint = { param($row) Get-Elements $row.children | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'HKCU' } }
        Navigate 'HKCU' 'HKEY_CURRENT_USER'
        Add-CurrentFavorite $name
        Navigate 'HKLM' 'HKEY_LOCAL_MACHINE'

        Edit-Address
        Invoke-Ui @('hover', 'OmniboxInput', '--dwell-time', '0') | Out-Null
        Wait-State { param($elements) $elements.name -contains $name -and $elements.name -notcontains $actions }
        $row = & $favoriteRow
        $caption = Get-Elements $row.children | Where-Object { $_.type -eq 'Text' -and $_.name -like "*$name*" } | Select-Object -First 1
        $hint = & $hiveHint $row
        $otherHint = Read-Ui | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'HKCR' }
        $rightEdge = $otherHint.x + $otherHint.width
        if (-not $hint -or -not $otherHint -or [Math]::Abs($hint.x + $hint.width - $rightEdge) -gt 1) {
            throw 'The hidden favorite action offset the hive alias.'
        }
        Invoke-Ui @('hover', $row.selector, '--dwell-time', '0') | Out-Null
        Invoke-Ui @('wait-for', $actions, '--type', 'Button', '-t', '5000') | Out-Null
        $elements = Read-Ui
        $hoveredRow = $elements | Where-Object selector -eq $row.selector
        $afterHover = $elements | Where-Object selector -eq $caption.selector
        $hoveredHint = & $hiveHint $hoveredRow
        $button = $elements | Where-Object { $_.type -eq 'Button' -and $_.name -eq $actions }
        if (-not $afterHover -or $afterHover.x -ne $caption.x -or -not $hoveredHint -or
            [Math]::Abs($hoveredHint.x + $hoveredHint.width - $rightEdge) -gt 1 -or $button.x + $button.width -gt $hoveredHint.x + 1) {
            throw 'Revealing the favorite action moved or covered the label or hive alias.'
        }
        if ($hoveredRow.y -ne $row.y -or $hoveredRow.height -ne $row.height -or
            $afterHover.y -ne $caption.y -or $hoveredHint.y -ne $hint.y) {
            throw 'Hovering the favorite changed the row height or shifted its text vertically.'
        }
        Expect-SuggestionSelection $row.selector $false
        Invoke-Ui @('hover', 'OmniboxInput', '--dwell-time', '0') | Out-Null
        Wait-State { param($elements) $elements.name -contains $name -and $elements.name -notcontains $actions }
        Invoke-Ui @('hover', $row.selector, '--dwell-time', '0') | Out-Null
        Invoke-Ui @('click', $actions) | Out-Null
        Invoke-Ui @('wait-for', 'Show in Favorites organizer', '--type', 'MenuItem', '-t', '5000') | Out-Null
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-KeyboardFocus 'OmniboxInput'

        Invoke-Ui @('hover', 'OmniboxInput', '--dwell-time', '0') | Out-Null
        Invoke-Ui @('send-keys', $name, '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-SuggestionRows $name @($name)
        $hint = & $hiveHint (& $favoriteRow)
        Invoke-Ui @('send-keys', 'Down', '--via', 'send-input') | Out-Null
        Invoke-Ui @('wait-for', $actions, '--type', 'Button', '-t', '5000') | Out-Null
        $selectedHint = & $hiveHint (& $favoriteRow)
        if (-not $hint -or -not $selectedHint -or $selectedHint.y -ne $hint.y) {
            throw 'Selecting the favorite with the keyboard shifted its hive alias.'
        }
        Expect-KeyboardFocus 'OmniboxInput'
        Invoke-Ui @('send-keys', 'shift+F10', '--via', 'send-input') | Out-Null
        Invoke-Ui @('wait-for', 'Show in Favorites organizer', '--type', 'MenuItem', '-t', '5000') | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input') | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -like "$name, HKEY_CURRENT_USER*" }).Count -eq 1
        }
        $bookmark = Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like "$name, HKEY_CURRENT_USER*" }
        Expect-SuggestionSelection $bookmark.selector
        Invoke-Ui @('send-keys', 'ctrl+w', '--via', 'send-input') | Out-Null
        Expect-Location 'HKEY_LOCAL_MACHINE'

        Enter-Scope '*' '' 'Favorites' 'Find favorites'
        Invoke-Ui @('send-keys', $name, '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        foreach ($confirmation in 'Cancel', 'Remove') {
            Expect-SuggestionRows $name @($name)
            $row = & $favoriteRow
            if ($confirmation -eq 'Cancel') {
                Invoke-Ui @('touch', $row.selector, '--gesture', 'long-press', '--hold-ms', '900') | Out-Null
            }
            else {
                Invoke-Ui @('hover', $row.selector, '--dwell-time', '0') | Out-Null
                Invoke-Ui @('click', $actions) | Out-Null
            }
            Invoke-Ui @('wait-for', 'Remove favorite…', '--type', 'MenuItem', '-t', '5000') | Out-Null
            $remove = Read-Ui | Where-Object { $_.type -eq 'MenuItem' -and $_.name -eq 'Remove favorite…' }
            Invoke-Ui @('hover', $remove.selector, '--dwell-time', '0') | Out-Null
            Invoke-Ui @('click', $remove.selector) | Out-Null
            Invoke-Ui @('wait-for', 'PrimaryButton', '--type', 'Button', '-t', '5000') | Out-Null
            $dialog = Read-Ui
            if ($dialog.automationId -contains 'OmniboxInput' -or $dialog.automationId -contains 'SuggestionsList') {
                throw 'The address popup is still visible over the removal confirmation.'
            }
            $choice = $dialog | Where-Object {
                $_.type -eq 'Button' -and $_.name -eq $confirmation -and $_.automationId -in 'CloseButton', 'PrimaryButton'
            }
            Invoke-Ui @('click', $choice.selector) | Out-Null
            Wait-State {
                param($elements)
                $elements.automationId -notcontains 'PrimaryButton' -and $elements.automationId -contains 'OmniboxInput'
            }
            Expect-KeyboardFocus 'OmniboxInput'
        }
        Expect-SuggestionRows $name @('No results found')
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-Location 'HKEY_LOCAL_MACHINE'
    }
    if ($results.Count -eq 0) { throw "No UI test matched '$TestFilter'." }
    Invoke-Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'completed.png')) | Out-Null
}
catch {
    if (-not $results.Where({ $_.status -eq 'FAIL' }).Count) { $results.Add(@{ name = 'UI test setup'; status = 'FAIL'; detail = "$_" }) }
    Write-Output "FAIL: $_"
}
finally {
    try { & winapp ui yield --on $Target | Out-Null }
    finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    @{ target = $Target; appPid = $AppPid; results = $results.ToArray() } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
    Write-Output "Results: $ArtifactDirectory"
}
if ($results.Where({ $_.status -eq 'FAIL' }).Count) { exit 1 }
