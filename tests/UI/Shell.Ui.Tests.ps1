param(
    [Parameter(Mandatory)]
    [int] $AppPid,
    [guid] $SandboxRun = [guid]::Empty,
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot "../../artifacts/shell-ui/$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
Assert-UiTestProcess $AppPid -SandboxRun $SandboxRun
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
    $output = @(& winapp ui @Arguments -w $Window --on local 2>&1)
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
    $json = Invoke-Ui @('inspect', '-d', '12', '--hide-offscreen', '--json')
    $json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Encoding utf8
    $inspection = $json | ConvertFrom-Json
    @(Get-Elements $inspection.windows[0].elements)
}

function Wait-State([scriptblock] $Check) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        try { if (& $Check @(Read-Ui)) { return } }
        catch { if ($_ -notmatch 'stale_element') { throw } }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw 'The expected UI state did not appear. See latest.json.'
}

function Navigate-Root([string] $Address, [string] $Title) {
    Invoke-Ui @('send-keys', 'ctrl+l', '--via', 'send-input') | Out-Null
    Invoke-Ui @('set-value', 'AddressInput', $Address) | Out-Null
    Invoke-Ui @('send-keys', 'enter', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
    Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $Title }).Count -eq 1 }
}

function Open-Menu {
    Invoke-Ui @('click', 'GlobalMenuButton') | Out-Null
    Read-Menu
}

function Read-Menu {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(& winapp ui list-windows -a $AppPid --on local --json | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0) { throw 'Could not list menu windows.' }
        $popup = @($windows | Where-Object { $_.label -eq 'popup' -and $_.ownerHwnd -eq $script:mainWindow })
        if ($popup.Count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 10)
    if ($popup.Count -ne 1) { throw 'Expected one menu popup.' }
    $inspection = Invoke-Ui @('inspect', '-d', '10', '--hide-offscreen', '--json') -Window $popup[0].hwnd | ConvertFrom-Json
    @{ window = $popup[0].hwnd; items = @(Get-Elements $inspection.windows[0].elements | Where-Object { $_.type -eq 'MenuItem' }) }
}

function Invoke-MenuItem($Menu, [string] $Name) {
    $item = @($Menu.items | Where-Object { $_.name -eq $Name })
    if ($item.Count -ne 1) { throw "Menu item not found: $Name" }
    Invoke-Ui @('invoke', $item[0].selector, '--action', 'invoke') -Window $Menu.window | Out-Null
}

function Test-Shell([string] $Name, [scriptblock] $Check) {
    try { & $Check; $results.Add(@{ name = $Name; status = 'PASS' }); Write-Output "PASS: $Name" }
    catch { $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" }); throw }
}

try {
    $windows = @(& winapp ui list-windows -a $AppPid --on local --json | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Could not find the app window.' }
    $appWindows = @($windows | Where-Object { $_.className -eq 'WinUIDesktopWin32WindowClass' })
    if ($appWindows.Count -ne 1) { throw 'Expected one local registry editor window.' }
    $script:mainWindow = $appWindows[0].hwnd
    $initial = @(Read-Ui)
    $initialTabCount = @($initial | Where-Object { $_.type -eq 'TabItem' }).Count
    if ($initialTabCount -ne 1 -or -not ($initial.automationId -contains 'Maximize')) { throw 'Start with one tab in a restored window.' }

    Test-Shell 'Application menu exposes global commands' {
        $menu = Open-Menu
        foreach ($name in @('New tab', 'Close tab', 'Tools', 'Import registry file…', 'Export entire registry…', 'Network registry', 'Open as administrator', 'Settings', 'Keyboard shortcuts', 'Help and about', 'Exit')) {
            if ($menu.items.name -notcontains $name) { throw "Missing global command: $name" }
        }
        if (@($menu.items | Where-Object name -in @('Watch this branch', 'Notes for this key', 'Modify…', 'Delete')).Count) { throw 'Application menu contains registry item actions.' }
        Invoke-MenuItem $menu 'New tab'
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' }).Count -eq $initialTabCount + 1 }
    }
    Test-Shell 'Title-bar tabs support pointer reordering' {
        $original = $initial | Where-Object { $_.type -eq 'TabItem' }
        $target = if ($original.name -eq 'HKEY_CURRENT_CONFIG') { 'HKEY_USERS' } else { 'HKEY_CURRENT_CONFIG' }
        Invoke-Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Invoke-Ui @('set-value', 'AddressInput', $target) | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $target }).Count -eq 1 }
        $tabs = @(Read-Ui | Where-Object { $_.type -eq 'TabItem' } | Sort-Object x)
        $y = [int]($tabs[1].y + $tabs[1].height / 2)
        Invoke-Ui @('drag', "$($tabs[1].x + 40),$y", "$($tabs[0].x + 12),$y", '--hold-ms', '250', '--dwell-ms', '250') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' } | Sort-Object x)[0].name -eq $target }
    }
    Test-Shell 'Dialogs follow the selected tab and its global menu remains available' {
        Invoke-Ui @('invoke', 'AdvancedSearchButton', '--action', 'invoke') | Out-Null
        Wait-State { param($elements) $elements.name -contains 'Find in registry' }
        $cancel = Read-Ui | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Cancel' }
        Invoke-Ui @('invoke', $cancel.selector, '--action', 'invoke') | Out-Null
        Invoke-MenuItem (Open-Menu) 'Close tab'
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' }).Count -eq $initialTabCount }
        $menu = Open-Menu
        if ($menu.items.name -notcontains 'Keyboard shortcuts') { throw 'Original tab did not regain its application menu.' }
        Invoke-Ui @('click', 'ToolsButton') | Out-Null
        Invoke-Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null
    }
    Test-Shell 'Tab context menus target the clicked tab and support keyboard access' {
        $original = @(Read-Ui | Where-Object type -eq 'TabItem')[0]
        Invoke-Ui @('click', $original.selector, '--right') | Out-Null
        $menu = Read-Menu
        foreach ($name in @('Duplicate tab', 'Close tab', 'Close other tabs', 'Close tabs to the right')) {
            if ($menu.items.name -notcontains $name) { throw "Missing tab command: $name" }
        }
        Invoke-Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'tab-context-menu.png')) -Window $menu.window | Out-Null
        if (@($menu.items | Where-Object { $_.name -in @('Close other tabs', 'Close tabs to the right') -and $_.isEnabled }).Count) {
            throw 'Bulk close commands must be disabled for a single tab.'
        }
        Invoke-MenuItem $menu 'Duplicate tab'
        Wait-State { param($elements) @($elements | Where-Object type -eq 'TabItem').Count -eq 2 }
        $target = if ($original.name -eq 'HKEY_USERS') { 'HKEY_CURRENT_CONFIG' } else { 'HKEY_USERS' }
        Invoke-Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Invoke-Ui @('set-value', 'AddressInput', $target) | Out-Null
        Invoke-Ui @('send-keys', 'Enter', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $target }).Count -eq 1 }
        $original = @(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $original.name })[0]
        Invoke-Ui @('click', $original.selector, '--right') | Out-Null
        Invoke-MenuItem (Read-Menu) 'Duplicate tab'
        Wait-State {
            param($elements)
            $tabs = @($elements | Where-Object type -eq 'TabItem' | Sort-Object x)
            $tabs.Count -eq 3 -and $tabs[0].name -eq $original.name -and $tabs[1].name -eq $original.name -and $tabs[2].name -eq $target
        }
        $original = @(Read-Ui | Where-Object type -eq 'TabItem' | Sort-Object x)[0]
        Invoke-Ui @('send-keys', 'shift+f10', '--via', 'send-input', '--target', $original.selector) | Out-Null
        Invoke-MenuItem (Read-Menu) 'Close tabs to the right'
        Wait-State { param($elements) @($elements | Where-Object type -eq 'TabItem').Count -eq 1 }
        Invoke-MenuItem (Open-Menu) 'Settings'
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Settings' }).Count -eq 1 }
        $settingsTab = @(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Settings' })[0]
        Invoke-Ui @('click', $settingsTab.selector, '--right') | Out-Null
        $menu = Read-Menu
        if ($menu.items.name -contains 'Duplicate tab' -or $menu.items.name -contains 'Move to bottom panel') {
            throw 'Settings must expose only the applicable tab commands.'
        }
        Invoke-Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null
        $original = @(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $original.name })[0]
        Invoke-Ui @('click', $original.selector, '--right') | Out-Null
        Invoke-MenuItem (Read-Menu) 'Close other tabs'
        Wait-State { param($elements) @($elements | Where-Object type -eq 'TabItem').Count -eq 1 -and $elements.name -notcontains 'Settings' }
    }
    Test-Shell 'Backspace navigates history in the list while preserving text editing' {
        Navigate-Root 'HKCU' 'HKEY_CURRENT_USER'
        Navigate-Root 'HKLM' 'HKEY_LOCAL_MACHINE'
        foreach ($keys in @('ctrl+backspace', 'shift+backspace', 'alt+backspace')) {
            Invoke-Ui @('send-keys', $keys, '--via', 'send-input', '--target', 'Items') | Out-Null
            if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'HKEY_LOCAL_MACHINE' }).Count -ne 1) {
                throw "$keys navigated history from the list."
            }
        }
        Invoke-Ui @('send-keys', 'backspace', '--via', 'send-input', '--target', 'Items') | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'HKEY_CURRENT_USER' }).Count -eq 1 }
        Navigate-Root 'HKLM' 'HKEY_LOCAL_MACHINE'
        Invoke-Ui @('send-keys', 'ctrl+l', '--via', 'send-input') | Out-Null
        Invoke-Ui @('set-value', 'AddressInput', 'HKLMx') | Out-Null
        Invoke-Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'AddressInput') | Out-Null
        Invoke-Ui @('send-keys', 'backspace', '--via', 'send-input') | Out-Null
        Invoke-Ui @('wait-for', 'AddressInput', '--value', 'HKLM', '-t', '3000') | Out-Null
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'HKEY_LOCAL_MACHINE' }).Count -ne 1) {
            throw 'Backspace in the address editor navigated history.'
        }
        Invoke-Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null
    }
    Test-Shell 'Backspace in the tree selects and focuses the parent instead of navigating history' {
        Navigate-Root 'HKCU' 'HKEY_CURRENT_USER'
        Navigate-Root 'HKLM\SOFTWARE' 'SOFTWARE'
        $node = @(Read-Ui | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'HKEY_LOCAL_MACHINE\SOFTWARE' })[0]
        if (-not $node) { throw 'Expected the current tree node.' }
        Invoke-Ui @('focus', $node.selector) | Out-Null
        foreach ($expected in @('HKEY_LOCAL_MACHINE', 'Computer', 'Computer')) {
            Invoke-Ui @('send-keys', 'backspace', '--via', 'send-input') | Out-Null
            Wait-State {
                param($elements)
                $parent = @($elements | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $expected })[0]
                if (-not $parent -or @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $expected }).Count -ne 1) { return $false }
                $properties = Invoke-Ui @('get-property', $parent.selector, '--json') | ConvertFrom-Json
                $properties | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'parent-properties.json') -Encoding utf8
                $properties.properties.IsSelected -eq 'True' -and $properties.properties.HasKeyboardFocus -eq 'True'
            }
        }
        Navigate-Root 'HKLM\SOFTWARE' 'SOFTWARE'
        $tree = Invoke-Ui @('inspect', '-d', '14', '--json') | ConvertFrom-Json
        $other = @(Get-Elements $tree.windows[0].elements | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'HKEY_CURRENT_USER' })[0]
        if (-not $other) { throw 'Expected the alternate tree node in the full tree.' }
        Invoke-Ui @('scroll-into-view', $other.selector) | Out-Null
        $other = @(Read-Ui | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'HKEY_CURRENT_USER' })[0]
        if (-not $other) { throw 'Expected the alternate tree node.' }
        Invoke-Ui @('invoke', $other.selector, '--action', 'select') | Out-Null
        Invoke-Ui @('send-keys', 'backspace', '--via', 'send-input', '--target', $other.selector) | Out-Null
        Wait-State { param($elements) @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Computer' }).Count -eq 1 }
    }
    Test-Shell 'Empty title-bar space drags the window' {
        Invoke-Ui @('focus', 'ToolsButton') | Out-Null
        $elements = @(Read-Ui)
        $before = $elements | Where-Object { $_.type -eq 'Window' } | Select-Object -First 1
        $add = $elements | Where-Object { $_.automationId -eq 'AddButton' }
        $minimize = $elements | Where-Object { $_.automationId -eq 'Minimize' }
        $x = [int](($add.x + $add.width + $minimize.x) / 2)
        $y = [int]($minimize.y + $minimize.height / 2)
        Invoke-Ui @('drag', "$x,$y", "$($x + 48),$($y + 32)") | Out-Null
        Wait-State {
            param($elements)
            $after = $elements | Where-Object { $_.type -eq 'Window' } | Select-Object -First 1
            [Math]::Abs($after.x - $before.x - 48) -le 2 -and [Math]::Abs($after.y - $before.y - 32) -le 2
        }
    }
    Test-Shell 'Native maximize and restore remain available' {
        Invoke-Ui @('click', 'Maximize') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'Restore' }
        Invoke-Ui @('click', 'Restore') | Out-Null
        Wait-State { param($elements) $elements.automationId -contains 'Maximize' }
    }
    Invoke-Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'completed.png')) | Out-Null
}
catch {
    if (-not $results.Where({ $_.status -eq 'FAIL' }).Count) { $results.Add(@{ name = 'UI test setup'; status = 'FAIL'; detail = "$_" }) }
    Write-Output "FAIL: $_"
}
finally {
    try { & winapp ui yield --on local | Out-Null }
    finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    @{ appPid = $AppPid; results = $results.ToArray() } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
    Write-Output "Results: $ArtifactDirectory"
}
if ($results.Where({ $_.status -eq 'FAIL' }).Count) { exit 1 }
