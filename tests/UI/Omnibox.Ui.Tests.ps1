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
    if ($Arguments -contains 'AddressInput') {
        $field = @(Read-Ui | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' })[0]
        if (-not $field) { throw 'Address editor is not visible.' }
        $Arguments = @($Arguments | ForEach-Object { if ($_ -eq 'AddressInput') { $field.selector } else { $_ } })
    }
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
        ($crumbs.name -join '\') -eq $breadcrumbAddress -and -not ($elements.name -contains 'Address')
    }
}

function Navigate([string] $Address, [string] $Expected) {
    Invoke-Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
    Invoke-Ui @('set-value', 'AddressInput', $Address) | Out-Null
    Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
    Expect-Location $Expected
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
            Invoke-Ui @('send-keys', 'HKCC', '--verbatim', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
            Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' -and $_.value -eq 'HKCC' }).Count -eq 1 }
            Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
            Expect-Location 'HKEY_CURRENT_USER'
        }
    }
    Test-Omnibox 'Invalid paths retain the draft and current key' {
        $missing = 'HKCU\Software\__Omnibox_Test_' + [guid]::NewGuid().ToString('N')
        Invoke-Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Invoke-Ui @('set-value', 'AddressInput', $missing) | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Wait-State {
            param($elements)
            $elements.automationId -contains 'Notification' -and
                @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' -and $_.value -eq $missing }).Count -eq 1
        }
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER'
    }
    Test-Omnibox 'Subkey suggestions can be chosen with the keyboard' {
        Invoke-Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Invoke-Ui @('send-keys', 'HKCU\Softw', '--verbatim', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Start-Sleep -Milliseconds 500
        Invoke-Ui @('send-keys', 'Down', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' -and $_.value -eq 'HKEY_CURRENT_USER\Software' }).Count -eq 1 }
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
        if ((Read-Ui).automationId -contains 'Notification') { throw 'Successful navigation did not clear the old error.' }
    }
    Test-Omnibox 'Escape prevents delayed suggestions from reopening the editor' {
        Invoke-Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Invoke-Ui @('send-keys', 'HKCR\', '--verbatim', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Start-Sleep -Milliseconds 500
        Expect-Location 'HKEY_CURRENT_USER\Software'
        $popups = @(& winapp ui list-windows -a $AppPid --json --on $Target | ConvertFrom-Json | Where-Object { $_.label -eq 'popup' })
        if ($LASTEXITCODE -ne 0 -or $popups.Count -gt 0) { throw 'A suggestion popup remained after cancelling.' }
    }
    Test-Omnibox 'Computer is the single root and its menu navigates to a hive' {
        $elements = @(Read-Ui)
        if ($elements.automationId -contains 'RootButton' -or
            -not @($elements | Where-Object { $_.automationId -eq 'Breadcrumb0' -and $_.name -eq 'Computer' }).Count) {
            throw 'Expected Computer as the first breadcrumb without a duplicate root selector.'
        }
        Invoke-Ui @('invoke', 'BreadcrumbChildren0', '--action', 'invoke') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'ChildLocations' }
        $menu = Invoke-Ui @('inspect', 'ChildLocations', '-d', '4', '--json') | ConvertFrom-Json
        $locationItem = Get-Elements $menu.windows[0].elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'HKEY_CURRENT_CONFIG' }
        Invoke-Ui @('invoke', $locationItem.selector, '--action', 'invoke') | Out-Null
        Expect-Location 'HKEY_CURRENT_CONFIG'
    }
    Test-Omnibox 'A segment chevron opens its children without navigating' {
        Navigate 'HKCU' 'HKEY_CURRENT_USER'
        Invoke-Ui @('invoke', 'BreadcrumbChildren1', '--action', 'invoke') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'ChildLocations' }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        do {
            $menu = Invoke-Ui @('inspect', 'ChildLocations', '-d', '4', '--json') | ConvertFrom-Json
            $locationItem = Get-Elements $menu.windows[0].elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Software' }
            if ($locationItem) { break }
            Start-Sleep -Milliseconds 100
        } while ($watch.Elapsed.TotalSeconds -lt 10)
        if (-not $locationItem) { throw 'The child menu did not show Software.' }
        Expect-Location 'HKEY_CURRENT_USER'
        Invoke-Ui @('scroll-into-view', $locationItem.selector) | Out-Null
        Invoke-Ui @('screenshot', 'ChildLocations', '--output', (Join-Path $ArtifactDirectory 'children.png')) | Out-Null
        Invoke-Ui @('invoke', $locationItem.selector, '--action', 'invoke') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
    }
    Test-Omnibox 'Down opens a segment menu and Escape restores its focus' {
        Wait-State { param($elements) $elements.automationId -notcontains 'ChildLocations' }
        Invoke-Ui @('send-keys', 'Down', '--via', 'send-input', '--target', 'Breadcrumb1') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'ChildLocations' }
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') | Out-Null
        Expect-Location 'HKEY_CURRENT_USER\Software'
        Wait-State {
            param($elements)
            $focus = Invoke-Ui @('get-property', 'Breadcrumb1', '-p', 'HasKeyboardFocus', '--json') | ConvertFrom-Json
            $focus.properties.HasKeyboardFocus -eq 'True'
        }
    }
    Test-Omnibox 'Large hive children support scrolling and keyboard navigation' {
        Navigate 'HKCR' 'HKEY_CLASSES_ROOT'
        Invoke-Ui @('send-keys', 'Down', '--via', 'send-input', '--target', 'Breadcrumb1') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'ChildLocations' }
        Invoke-Ui @('send-keys', 'End', '--via', 'send-input', '--target', 'ChildLocations') | Out-Null
        $menu = Invoke-Ui @('inspect', 'ChildLocations', '-d', '4', '--hide-offscreen', '--json') | ConvertFrom-Json
        $visible = @(Get-Elements $menu.windows[0].elements | Where-Object { $_.type -eq 'ListItem' })
        if ($visible.Count -eq 0 -or $visible.Count -gt 20) { throw 'Expected a bounded viewport of large-hive children.' }
        Invoke-Ui @('screenshot', 'ChildLocations', '--output', (Join-Path $ArtifactDirectory 'large-hive.png')) | Out-Null
        $destination = 'HKEY_CLASSES_ROOT\' + $visible[-1].name
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input') | Out-Null
        Expect-Location $destination
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
