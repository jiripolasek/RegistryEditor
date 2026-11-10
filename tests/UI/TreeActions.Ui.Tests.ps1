param(
    [string] $Executable,
    [string] $TestDataDirectory,
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot "../../artifacts/tree-actions-ui/$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
Assert-UiTestBuild $Executable
Assert-UiTestPath $ArtifactDirectory
$profile = New-UiTestProfile $TestDataDirectory
$Executable = [IO.Path]::GetFullPath($Executable)
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$fixtureName = '!Regedit.TreeUi.' + [guid]::NewGuid().ToString('N')
$fixturePath = "HKEY_CURRENT_USER\Software\$fixtureName"
$targetPath = "$fixturePath\BranchA"
$settingsFile = Join-Path $profile 'settings.json'
$appProcess = $null
$mainWindow = 0

function Invoke-WinApp([string[]] $Arguments) {
    $output = @(& winapp @Arguments --on local 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "winapp $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)" }
    $output
}

function Invoke-Ui([string[]] $Arguments, [long] $Window = $script:mainWindow) {
    Invoke-WinApp (@('ui') + $Arguments + @('-w', "$Window"))
}

function Get-Elements($Nodes) {
    foreach ($node in $Nodes) {
        $node
        if ($node.children) { Get-Elements $node.children }
    }
}

function Read-Ui([long] $Window = $script:mainWindow) {
    $json = Invoke-Ui @('inspect', '-d', '14', '--hide-offscreen', '--json') -Window $Window
    $json | Set-Content (Join-Path $ArtifactDirectory 'latest.json') -Encoding utf8
    $tree = $json | ConvertFrom-Json
    @(Get-Elements $tree.windows[0].elements)
}

function Wait-Ui([scriptblock] $Check) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Check @(Read-Ui)) { return }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw 'Expected UI state did not appear.'
}

function Find-Node([string] $Path) {
    $node = @(Read-Ui | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $Path })
    if ($node.Count -ne 1) { throw "Expected one visible tree node: $Path" }
    $node[0]
}

function Wait-Window([scriptblock] $Check) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(Invoke-WinApp @('ui', 'list-windows', '--json') | ConvertFrom-Json | Where-Object $Check)
        if ($windows.Count -eq 1) { return $windows[0] }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw 'Expected owned window did not appear.'
}

function Open-Menu([string] $Path, [switch] $Keyboard) {
    $node = Find-Node $Path
    if ($Keyboard) { Invoke-Ui @('send-keys', 'shift+f10', '--target', $node.selector, '--via', 'send-input') | Out-Null }
    else { Invoke-Ui @('click', $node.selector, '--right') | Out-Null }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $popups = @(Invoke-WinApp @('ui', 'list-windows', '--json') | ConvertFrom-Json | Where-Object { $_.ownerHwnd -eq $script:mainWindow -and $_.label -eq 'popup' })
        foreach ($popup in $popups) {
            $items = @(Read-Ui -Window $popup.hwnd | Where-Object type -eq 'MenuItem')
            if ($items.Count) { return @{ Window = $popup.hwnd; Items = $items } }
        }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw 'Expected context menu did not appear.'
}

function Test-Tree([string] $Name, [scriptblock] $Check) {
    try { & $Check; $results.Add(@{ name = $Name; status = 'PASS' }); Write-Output "PASS: $Name" }
    catch { $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" }); throw }
}

try {
    $fixture = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\$fixtureName")
    try {
        $fixture.SetValue('ParentMarker', 'parent')
        $branch = $fixture.CreateSubKey('BranchA\Leaf'); $branch.Dispose()
        $branch = $fixture.OpenSubKey('BranchA', $true)
        try { $branch.SetValue('TargetMarker', 'branch-a') } finally { $branch.Dispose() }
        $branch = $fixture.CreateSubKey('BranchB'); $branch.Dispose()
    } finally { $fixture.Dispose() }
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.Environment['REGEDIT_UI_TEST_DATA'] = $profile
    $start.ArgumentList.Add($fixturePath)
    $appProcess = [Diagnostics.Process]::Start($start)
    $appProcess.Id | Set-Content (Join-Path $ArtifactDirectory 'process.txt')
    $appProcess.WaitForInputIdle(15000) | Out-Null
    $window = Wait-Window { $_.processId -eq $appProcess.Id -and $_.className -eq 'WinUIDesktopWin32WindowClass' }
    $script:mainWindow = $window.hwnd
    Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $fixturePath }).Count -eq 1 }

    Test-Tree 'Double-click expands and collapses the node' {
        $node = Find-Node $fixturePath
        Invoke-Ui @('invoke', $node.selector, '--action', 'collapse') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $targetPath }).Count -eq 0 }
        $node = Find-Node $fixturePath
        Invoke-Ui @('click', $node.selector, '--double') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $targetPath }).Count -eq 1 }
        $node = Find-Node $fixturePath
        Invoke-Ui @('click', $node.selector, '--double') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $targetPath }).Count -eq 0 }
        $node = Find-Node $fixturePath
        Invoke-Ui @('click', $node.selector, '--double') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $targetPath }).Count -eq 1 }
    }

    Test-Tree 'Right-click exposes the requested key actions' {
        $script:menu = Open-Menu $targetPath
        foreach ($name in @('Open in new tab', 'Add to favorites…', 'Export…', 'Permissions…', 'Copy key path')) {
            if ($menu.Items.name -notcontains $name) { throw "Missing context command: $name" }
        }
        Invoke-Ui @('screenshot', '--capture-screen', '--output', (Join-Path $ArtifactDirectory 'context-menu.png')) | Out-Null
    }

    Test-Tree 'Tree favorite is persisted for the requested key' {
        Invoke-Ui @('invoke', 'TreeFavoriteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        $name = @(Read-Ui | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Name' })
        if ($name.Count -ne 1) { throw 'Expected favorite name field.' }
        Invoke-Ui @('set-value', $name[0].selector, 'Tree context test') | Out-Null
        Invoke-Ui @('invoke', 'PrimaryButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $saved = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        $favorites = @($saved.Favorites | Where-Object Address -eq $targetPath)
        if ($favorites.Count -ne 1 -or $favorites[0].Is32Bit -or $favorites[0].Name -ne 'Tree context test') { throw 'Favorite has incorrect identity or was not saved.' }
    }

    Test-Tree 'Tree unfavorite removes the requested key' {
        $menu = Open-Menu $targetPath
        if ($menu.Items.name -notcontains 'Remove from favorites') { throw 'Favorite action did not update.' }
        Invoke-Ui @('invoke', 'TreeFavoriteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.name -notcontains 'Tree context test' }
        $saved = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        if (@($saved.Favorites | Where-Object Address -eq $targetPath).Count) { throw 'Favorite was not removed from settings.' }
    }

    Test-Tree 'Keyboard context menu exports only the target branch' {
        $menu = Open-Menu $targetPath -Keyboard
        Invoke-Ui @('invoke', 'TreeExportMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        $picker = Wait-Window { $_.ownerHwnd -eq $script:mainWindow -and $_.processName -eq 'PickerHost' }
        $controls = @(Read-Ui -Window $picker.hwnd)
        $field = @($controls | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'File name:' })
        $save = @($controls | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq '1' -and $_.name -eq 'Save' })
        if ($field.Count -ne 1 -or $save.Count -ne 1) { throw 'Missing save picker controls.' }
        $export = Join-Path $ArtifactDirectory 'tree-branch.reg'
        Invoke-Ui @('focus', $field[0].selector) -Window $picker.hwnd | Out-Null
        Invoke-Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $field[0].selector) -Window $picker.hwnd | Out-Null
        Invoke-Ui @('send-keys', $export, '--verbatim', '--via', 'send-input', '--target', $field[0].selector) -Window $picker.hwnd | Out-Null
        $typed = @(Read-Ui -Window $picker.hwnd | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'File name:' })
        if ($typed.Count -ne 1 -or $typed[0].value -cne $export) { throw 'The Save picker did not accept the isolated export path.' }
        Invoke-Ui @('invoke', $save[0].selector, '--action', 'invoke') -Window $picker.hwnd | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Export complete' }
        if ((Read-Ui).name -notcontains $export) { throw 'The app reported a different export destination.' }
        $text = Get-Content -LiteralPath $export -Raw
        if (-not $text.Contains("[$targetPath]") -or -not $text.Contains('"TargetMarker"=') -or $text.Contains('ParentMarker') -or $text.Contains('BranchB')) {
            throw 'Export did not stay within the context key.'
        }
    }

    Test-Tree 'Permissions dialog belongs to the context key' {
        $menu = Open-Menu $targetPath
        Invoke-Ui @('click', 'TreePermissionsMenu') -Window $menu.Window | Out-Null
        $permissions = Wait-Window { $_.processId -eq $appProcess.Id -and $_.className -eq '#32770' -and $_.title -like '*Permissions*' }
        if (-not $permissions.title.Contains($targetPath)) { throw 'Permissions dialog has the wrong key name.' }
        $cancel = @(Read-Ui -Window $permissions.hwnd | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Cancel' })
        if ($cancel.Count -ne 1) { throw 'Expected permissions Cancel button.' }
        Invoke-Ui @('invoke', $cancel[0].selector, '--action', 'invoke') -Window $permissions.hwnd | Out-Null
    }

    Test-Tree 'Right-clicking empty tree space does not target the last node' {
        $node = Find-Node 'HKEY_CURRENT_USER'
        Invoke-Ui @('invoke', $node.selector, '--action', 'collapse') | Out-Null
        $items = @(Read-Ui)
        $tree = $items | Where-Object automationId -eq 'RegistryTree'
        $centerY = $tree.y + $tree.height / 2
        if (@($items | Where-Object { $_.type -eq 'TreeItem' -and $_.y -le $centerY -and ($_.y + $_.height) -gt $centerY }).Count) {
            throw 'The tree center must be empty for this check.'
        }
        Invoke-Ui @('click', 'RegistryTree', '--right') | Out-Null
        $popups = @(Invoke-WinApp @('ui', 'list-windows', '--json') | ConvertFrom-Json | Where-Object { $_.ownerHwnd -eq $script:mainWindow -and $_.label -eq 'popup' })
        foreach ($popup in $popups) {
            if (@(Read-Ui -Window $popup.hwnd | Where-Object type -eq 'MenuItem').Count) { throw 'Empty tree space opened a stale node menu.' }
        }
    }
}
catch {
    if (-not $results.Where({ $_.status -eq 'FAIL' }).Count) { $results.Add(@{ name = 'Tree test setup'; status = 'FAIL'; detail = "$_" }) }
    Write-Output "FAIL: $_"
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(10000)) {
            $results.Add(@{ name = 'Close test editor'; status = 'FAIL'; detail = 'The fixture editor did not exit.' })
            $appProcess.Kill()
            $appProcess.WaitForExit()
        }
    }
    $parent = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software', $true)
    try { $parent.DeleteSubKeyTree($fixtureName, $false) } finally { $parent.Dispose() }
    try { Invoke-WinApp @('ui', 'yield') | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    @{ results = $results.ToArray(); target = 'local' } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
    Write-Output "Results: $ArtifactDirectory"
}
if ($results.Where({ $_.status -eq 'FAIL' }).Count) { exit 1 }
