param(
    [Parameter(Mandatory)]
    [int] $AppPid,
    [ValidateSet('local', 'sandbox')]
    [string] $Target = 'local',
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

function Expect-SuggestionSelection([string] $Selector, [bool] $Selected = $true) {
    $item = Invoke-Ui @('get-property', $Selector, '-p', 'IsSelected', '--json') | ConvertFrom-Json
    if ($item.properties.IsSelected -ne $Selected.ToString()) { throw "Expected $Selector selection to be $Selected." }
}

function Test-Omnibox([string] $Name, [scriptblock] $Check) {
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

    Test-Omnibox 'Typed aliases become clickable breadcrumbs' {
        Navigate 'HKCU\Software' 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'Ancestor navigation updates the current location' {
        $ancestor = Read-Ui | Where-Object { $_.automationId -eq 'Breadcrumb1' -and $_.name -eq 'HKEY_CURRENT_USER' }
        Invoke-Ui @('invoke', $ancestor.selector, '--action', 'invoke') | Out-Null
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
            Invoke-Ui @('send-keys', $key, '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
            $expected = (Invoke-Ui @('get-focused', '--json') | ConvertFrom-Json).element.automationId
            if (-not $expected -or $expected -eq 'EditAddressButton') { throw "Could not find the $key target from Edit address." }

            Invoke-Ui @('send-keys', 'alt+d', '--via', 'send-input', '--target', 'EditAddressButton') | Out-Null
            Wait-State {
                param($elements)
                $elements.automationId -contains 'OmniboxInput' -and $elements.automationId -notcontains 'OmniboxSuggestionFooter'
            }
            Expect-KeyboardFocus 'OmniboxInput'
            Invoke-Ui @('send-keys', $key, '--via', 'send-input') | Out-Null
            $actual = (Invoke-Ui @('get-focused', '--json') | ConvertFrom-Json).element.automationId
            if ($actual -ne $expected) { throw "$key from the omnibox focused '$actual'. Expected '$expected'." }
            Expect-Location 'HKEY_CURRENT_USER'
        }
    }

    Test-Omnibox 'Invalid paths retain the draft and current key' {
        $missing = 'HKCU\Software\__Omnibox_Test_' + [guid]::NewGuid().ToString('N')
        Edit-Address
        Invoke-Ui @('set-value', 'OmniboxInput', $missing) | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State {
            param($elements)
            @($elements | Where-Object { $_.automationId -like 'Notification_*' }).Count -gt 0 -and
                @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' -and $_.value -eq $missing }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER'
    }
    Test-Omnibox 'Subkey suggestions can be chosen with the keyboard' {
        Edit-Address
        Invoke-Ui @('send-keys', 'HKCU\Softw', '--verbatim', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'OmniboxSuggestionFooter' }
        foreach ($key in @('Tab', 'Down')) {
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
        if (@((Read-Ui) | Where-Object { $_.automationId -like 'Notification_*' }).Count -gt 0) { throw 'Successful navigation did not clear the old error.' }
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
        Invoke-Ui @('send-keys', 'ctrl+a Backspace', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
        Invoke-Ui @('send-keys', 'Down Enter', '--via', 'send-input', '--target', 'OmniboxInput') | Out-Null
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
        Wait-State { param($elements) $elements.automationId -contains 'FilteredMenuItems' }
        $popup = & winapp ui list-windows -a $AppPid --json --on $Target | ConvertFrom-Json | Where-Object label -eq 'popup' | Select-Object -First 1
        Invoke-Ui @('send-keys', 'Down End', '--via', 'send-input') | Out-Null
        Wait-State {
            param($elements)
            $menu = $elements | Where-Object automationId -eq 'FilteredMenuItems'
            $visible = @(Get-Elements $menu.children | Where-Object type -eq 'MenuItem')
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
