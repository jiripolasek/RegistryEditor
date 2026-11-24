param(
    [Parameter(Mandatory)][string] $Executable,
    [string] $TestFilter = '.',
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot "../../artifacts/note-close-ui/$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
Assert-UiTestBuild $Executable
$Executable = [IO.Path]::GetFullPath($Executable)
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { if ([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this profile-isolated UI suite without elevation.' } }
finally { $identity.Dispose() }
$ArtifactDirectory = New-UiTestProfile $ArtifactDirectory
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$appProcess = $null
$mainWindow = 0
$settingsFile = $null
$savedText = 'Saved close-test note'
$draftText = 'Unsaved close-test draft'
$noteSaveLease = $null

. (Join-Path $PSScriptRoot 'UiTestWindow.ps1')

function Invoke-WinAppChecked([string[]] $Arguments) {
    $output = @(& winapp @Arguments --on local 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "winapp $($Arguments -join ' '): $($output -join [Environment]::NewLine)" }
    $output
}
function Ui([string[]] $Arguments, [long] $Window = $script:mainWindow) {
    if (-not $Window -or -not $appProcess -or $appProcess.HasExited) { throw 'The isolated test window is not running.' }
    Invoke-WinAppChecked (@('ui') + $Arguments + @('-w', "$Window"))
}
function Elements($Nodes) { foreach ($node in $Nodes) { $node; if ($node.children) { Elements $node.children } } }
function Read-Ui([long] $Window = $script:mainWindow) {
    $json = Ui @('inspect', '-d', '14', '--json', '--hide-offscreen') -Window $Window
    $json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Encoding utf8
    $tree = $json | ConvertFrom-Json
    @(Elements $tree.windows[0].elements)
}
function Wait-Ui([scriptblock] $Check) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do { if (& $Check @(Read-Ui)) { return }; Start-Sleep -Milliseconds 100 } while ($timer.Elapsed.TotalSeconds -lt 15)
    throw 'Expected UI state did not appear.'
}
function Pick([string] $Type, [string] $Name, [long] $Window = $script:mainWindow) {
    $items = @(Read-Ui -Window $Window | Where-Object { $_.type -eq $Type -and $_.name -eq $Name })
    if ($items.Count -ne 1) { throw "Expected one $Type named '$Name', found $($items.Count)." }
    $items[0]
}
function Press([string] $Name) { Ui @('invoke', (Pick 'Button' $Name).selector, '--action', 'invoke') | Out-Null }
function Focus-NoteEditor {
    Set-TestWindowForeground
    Ui @('focus', 'NoteEditor') | Out-Null
}
function Keys([string] $Keys) {
    Focus-NoteEditor
    if ([UxTestWindow]::GetForegroundWindow() -ne [IntPtr]$mainWindow) { throw 'The isolated test window must be foreground for keyboard input.' }
    Ui @('send-keys', $Keys, '--via', 'send-input') | Out-Null
}
function Open-Menu([string] $Name) {
    Press 'Application menu'
    $windows = Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json
    $popup = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.label -eq 'popup' } | Sort-Object height -Descending)[0]
    if (-not $popup) { throw 'Missing isolated application menu.' }
    $item = Pick 'MenuItem' $Name -Window $popup.hwnd
    Ui @('invoke', $item.selector, '--action', 'invoke') -Window $popup.hwnd | Out-Null
}
function Start-Case([string] $Name, [bool] $Dirty = $true) {
    if ($appProcess -and -not $appProcess.HasExited) { throw 'Previous isolated case is still running.' }
    if ($appProcess) { $appProcess.Dispose() }
    if ($noteSaveLease) { $noteSaveLease.Dispose(); $script:noteSaveLease = $null }
    $profile = Join-Path $ArtifactDirectory $Name
    New-Item -ItemType Directory -Path $profile | Out-Null
    $script:settingsFile = Join-Path $profile 'settings.json'
    @{ Notes = @(@{ Location = @{ Hive = -2147483647; SubKey = 'Software'; View = 256 }; Text = $savedText }); SelectedTool = 'Notes'; ToolPanePinned = $true } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsFile -Encoding utf8
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.ArgumentList.Add('HKCU\Software')
    $start.Environment['REGEDIT_UI_TEST_DATA'] = $profile
    $script:appProcess = [Diagnostics.Process]::Start($start)
    $script:mainWindow = 0
    $appProcess.WaitForInputIdle(15000) | Out-Null
    Invoke-WinAppChecked @('ui', 'wait-for', 'ToolsButton', '-a', "$($appProcess.Id)") | Out-Null
    $windows = Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json
    $window = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.className -eq 'WinUIDesktopWin32WindowClass' })[0]
    if (-not $window) { throw 'The isolated editor did not open.' }
    $script:mainWindow = [long]$window.hwnd
    [UxTestWindow]::TargetWindow = [IntPtr]$mainWindow
    Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq $savedText }).Count -eq 1 }
    if ($Dirty) {
        $script:noteSaveLease = [IO.File]::Open($settingsFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        Ui @('set-value', 'NoteEditor', $draftText) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Unsaved changes' -or $items.name -contains "Couldn’t save" }
    }
}
function Request-Close([string] $Route) {
    switch ($Route) {
        'Exit' { Open-Menu 'Close application' }
        'CtrlW' { Keys 'ctrl+w' }
        'TabButton' {
            $tab = Pick 'TabItem' 'Software'
            $close = @(Elements $tab.children | Where-Object { $_.type -eq 'Button' -and ($_.automationId -eq 'CloseButton' -or $_.name -like 'Close*') })
            if ($close.Count -ne 1) { throw 'Expected one close button in the last tab.' }
            Ui @('invoke', $close[0].selector, '--action', 'invoke') | Out-Null
        }
        'MiddleClick' {
            Focus-NoteEditor
            $tab = Pick 'TabItem' 'Software'
            [UxTestWindow]::MiddleClickAt( [int]($tab.x + $tab.width / 2), [int]($tab.y + $tab.height / 2))
        }
        'AltF4' {
            Focus-NoteEditor
            if ([UxTestWindow]::GetForegroundWindow() -ne [IntPtr]$mainWindow) { throw 'The isolated editor must be foreground before Alt+F4.' }
            Ui @('send-keys', 'alt+f4', '--via', 'send-input', '--target', 'NoteEditor', '--allow-system-keys') | Out-Null
        }
        'IconDoubleClick' {
            Set-TestWindowForeground
            $caption = Get-TestActivationPoint
            [UxTestWindow]::LeftClickAt($caption.X, $caption.Y)
            $icon = Pick 'Image' 'Modern Registry Editor'
            [UxTestWindow]::DoubleClickAt([int]($icon.x + $icon.width / 2), [int]($icon.y + $icon.height / 2))
        }
        'Caption' {
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq 'Close' }).Count -eq 1 }
            $close = @(Read-Ui | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq 'Close' })
            if ($close.Count -ne 1) { throw 'Expected one native caption Close button.' }
            Ui @('invoke', $close[0].selector, '--action', 'invoke') | Out-Null
        }
        default { throw "Unknown close route: $Route" }
    }
}
function Expect-Draft {
    if ($appProcess.HasExited) { throw 'Close cancellation destroyed the window.' }
    Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq $draftText }).Count -eq 1 -and ($items.name -contains 'Unsaved changes' -or $items.name -contains "Couldn’t save") }
    if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes[0].Text -ne $savedText) { throw 'Cancel or failed save changed the persisted note.' }
}
function Finish-Case([bool] $Save) {
    if ($noteSaveLease) { $noteSaveLease.Dispose(); $script:noteSaveLease = $null }
    Press $(if ($Save) { 'Save notes' } else { 'Discard notes' })
    if (-not $appProcess.WaitForExit(15000)) { throw 'Accepted close did not finish.' }
    $expected = if ($Save) { $draftText } else { $savedText }
    if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes[0].Text -ne $expected) { throw 'Accepted close persisted the wrong note text.' }
}
function Test-Ui([string] $Name, [scriptblock] $Action) {
    if ($Name -notmatch $TestFilter) { return }
    try { & $Action | Out-Null; $results.Add(@{ name = $Name; status = 'PASS' }); Write-Output "PASS: $Name" }
    catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_"; stack = $_.ScriptStackTrace })
        Write-Output "FAIL: ${Name}: $_"
        if (Test-Path -LiteralPath (Join-Path $ArtifactDirectory 'latest.json')) {
            Copy-Item -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Destination (Join-Path $ArtifactDirectory ("failure-" + $results.Count + '.json'))
        }
        if ($appProcess -and -not $appProcess.HasExited) { $appProcess.Kill(); $appProcess.WaitForExit() }
    }
}

try {
    Test-Ui 'Notes autosave after typing and hide redundant sidebar actions' {
        Start-Case 'Autosave' $false
        $items = Read-Ui
        if ($items.name -contains 'Open key' -or $items.name -contains 'Note for current key' -or
            $items.name -notcontains 'This computer · 64-bit registry') {
            throw 'The following sidebar has redundant actions or omits its registry scope.'
        }
        Ui @('set-value', 'NoteEditor', 'Autosaved MVP note') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Saved' -and @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Autosaved MVP note' }).Count -eq 1 }
        if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes[0].Text -cne 'Autosaved MVP note') {
            throw 'Typing did not persist the note without Save.'
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'notes-autosave.png')) | Out-Null
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(15000)) { throw 'A saved note unnecessarily prompted on close.' }
    }
    Test-Ui 'Closing flushes pending Notes autosave without prompting' {
        Start-Case 'FlushOnClose' $false
        Ui @('set-value', 'NoteEditor', $draftText) | Out-Null
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(15000)) { throw 'Pending autosave was not flushed on close.' }
        if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes[0].Text -cne $draftText) {
            throw 'Closing lost the pending note edit.'
        }
    }
    Test-Ui 'Notes context menu deletion confirmation and Undo preserve text' {
        Start-Case 'DeleteAndUndo' $false
        Ui @('invoke', 'ExpandToolButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'NoteTitle' }
        $row = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Software,*' })[0]
        if (-not $row) { throw 'The saved note row is missing.' }
        Ui @('focus', $row.selector) | Out-Null
        Ui @('click', $row.selector, '--right') | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            $windows = Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json
            $popup = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.label -eq 'popup' } | Sort-Object height -Descending)[0]
            $items = if ($popup) { @(Read-Ui -Window $popup.hwnd) } else { @() }
            if ($items.name -contains 'Edit note') { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($items.name -notcontains 'Edit note') { throw 'The Notes context menu did not open.' }
        foreach ($name in 'Edit note', 'Open key', 'Copy path', 'Copy note', 'Delete note') {
            if ($items.name -notcontains $name) { throw "Missing Notes menu item: $name" }
        }
        $edit = @($items | Where-Object name -eq 'Edit note')[0]
        Ui @('invoke', $edit.selector, '--action', 'invoke') -Window $popup.hwnd | Out-Null
        foreach ($accept in $false, $true) {
            Ui @('send-keys', 'delete', '--via', 'send-input', '--target', $row.selector) | Out-Null
            Wait-Ui { param($items) $items.name -contains 'Delete note?' }
            Press $(if ($accept) { 'Delete note' } else { 'Cancel' })
            if (-not $accept -and (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes[0].Text -cne $savedText) {
                throw 'Canceling Delete changed the saved note.'
            }
        }
        Wait-Ui { param($items) $items.name -contains 'No note for this key' -and $items.automationId -contains 'UndoNoteDelete' }
        if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes.Count -ne 0) { throw 'Delete did not persist removal.' }
        Ui @('invoke', 'UndoNoteDelete') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Saved' -and @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq $savedText }).Count -eq 1 }
        if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes[0].Text -cne $savedText) { throw 'Undo did not restore the saved text.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'notes-organizer.png')) | Out-Null
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(15000)) { throw 'The Notes tab could not close after Undo.' }
    }
    Test-Ui 'IconDoubleClick closes a clean window' {
        Start-Case 'IconDoubleClickClean' $false
        Request-Close 'IconDoubleClick'
        if (-not $appProcess.WaitForExit(15000)) { throw 'Double-clicking the icon did not close the clean window.' }
        if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes[0].Text -ne $savedText) {
            throw 'Double-clicking the icon changed the saved note.'
        }
    }

    $index = 0
    foreach ($route in @('Exit', 'CtrlW', 'TabButton', 'MiddleClick', 'AltF4', 'Caption', 'IconDoubleClick')) {
        Test-Ui "$route guards failed autosave, preserves Cancel, and honors Save/Discard" {
            Start-Case $route
            Request-Close $route
            Wait-Ui { param($items) $items.name -contains 'Unsaved notes' }
            $appProcess.CloseMainWindow() | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Save notes' }).Count -eq 1 }
            Press 'Cancel'
            Expect-Draft
            Request-Close $route
            Wait-Ui { param($items) $items.name -contains 'Unsaved notes' }
            Finish-Case ($index % 2 -eq 0)
        }
        $index++
    }
    Test-Ui 'Failed close-save retains the dialog and draft, then retries successfully' {
        Start-Case 'SaveFailure'
        Open-Menu 'Close application'
        Wait-Ui { param($items) $items.name -contains 'Unsaved notes' }
        Press 'Save notes'
        Wait-Ui { param($items) @($items | Where-Object name -like 'Could not save notes:*').Count -gt 0 -and $items.name -contains 'Unsaved notes' }
        if ($appProcess.HasExited) { throw 'A failed save closed the window.' }
        Press 'Cancel'
        Expect-Draft
        Open-Menu 'Close application'
        Wait-Ui { param($items) $items.name -contains 'Unsaved notes' }
        Finish-Case $true
    }
    Test-Ui 'Failed session save keeps the last tab and discarded draft available for retry' {
        Start-Case 'SessionFailure'
        Open-Menu 'Close application'
        Wait-Ui { param($items) $items.name -contains 'Unsaved notes' }
        Press 'Discard notes'
        Wait-Ui { param($items) $items.name -contains "Couldn't close the window" }
        Expect-Draft
        if ((Pick 'TabItem' 'Software').isOffscreen) { throw 'The registry tab was lost after the failed session save.' }
        Open-Menu 'Close application'
        Wait-Ui { param($items) $items.name -contains 'Unsaved notes' }
        Finish-Case $false
    }
    Test-Ui 'Clean window close preserves its open dialog and remains retryable' {
        Start-Case 'CleanOtherDialog' $false
        Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Add favorite' }
        $appProcess.CloseMainWindow() | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Add favorite' -and $items.name -contains "Can't close yet" }
        if ($appProcess.HasExited) { throw 'A clean window closed behind its open dialog.' }
        Press 'Cancel'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq $savedText }).Count -eq 1 }
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(15000)) { throw 'The clean window could not close after its dialog was canceled.' }
        if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Notes[0].Text -ne $savedText) { throw 'Closing a clean window changed its saved note.' }
    }
    Test-Ui 'Close during another dialog preserves its dialog and the unsaved note' {
        Start-Case 'OtherDialog'
        Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Add favorite' }
        $appProcess.CloseMainWindow() | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Add favorite' -and $items.name -contains "Can't close yet" }
        Press 'Cancel'
        Expect-Draft
        Open-Menu 'Close application'
        Wait-Ui { param($items) $items.name -contains 'Unsaved notes' }
        Finish-Case $false
    }
} catch {
    if ($mainWindow -and $appProcess -and -not $appProcess.HasExited) { try { Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'failure.png')) | Out-Null } catch { } }
    if (-not $results.Where({ $_.status -eq 'FAIL' }).Count) { $results.Add(@{ name = 'Setup'; status = 'FAIL'; detail = "$_" }) }
    Write-Output "FAIL: $_"
} finally {
    if ($noteSaveLease) { $noteSaveLease.Dispose() }
    if ($appProcess) {
        # Only the process launched by this script is eligible for cleanup.
        if (-not $appProcess.HasExited) { $appProcess.Kill(); $appProcess.WaitForExit() }
        $appProcess.Dispose()
    }
    try { Invoke-WinAppChecked @('ui', 'yield') | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    @{ results = $results.ToArray(); target = 'local'; executable = $Executable } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
}
if (-not $results.Count) { throw 'No close-protection tests matched the filter.' }
if ($results.Where({ $_.status -eq 'FAIL' }).Count) { exit 1 }
