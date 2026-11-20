param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int] $AppPid,
    [Parameter(Mandatory)][guid] $SandboxId,
    [Parameter(Mandatory)][string] $FixtureAddress,
    [Parameter(Mandatory)][string] $ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
if ($FixtureAddress -notmatch '^HKEY_CURRENT_USER\\Software\\RegeditModern\.UiUndo_[a-f0-9]{32}$') {
    throw 'Use a disposable RegeditModern.UiUndo_<guid> registry fixture in the Sandbox.'
}
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$previousSandbox = $env:WINAPP_EXPECT_SANDBOX
$env:WINAPP_UI_WORKFLOW_ID = 'regedit-undo-redo-' + $SandboxId.ToString('N')
$env:WINAPP_EXPECT_SANDBOX = $SandboxId.ToString()
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) { throw 'Choose a fresh artifact directory.' }
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null
$results = [Collections.Generic.List[object]]::new()
$fixture = $FixtureAddress.Substring('HKEY_CURRENT_USER\'.Length)

function Ui([string[]] $Arguments) {
    $output = @(& winapp ui @Arguments --on sandbox -a $AppPid --json 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
    ($output -join [Environment]::NewLine) | ConvertFrom-Json
}

function Nodes($Items) {
    foreach ($item in $Items) {
        $item
        if ($item.children) { Nodes $item.children }
    }
}

function Read-Ui {
    $state = Ui @('inspect', '--depth', '20', '--hide-offscreen')
    $state | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Encoding utf8
    @(Nodes $state.windows[0].elements)
}

function Wait-Ui([scriptblock] $Check) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Check @(Read-Ui)) { return }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 12)
    throw 'Expected UI state did not appear. See latest.json.'
}

function Keys([string] $Text, [string] $Target) {
    $arguments = @('send-keys', $Text, '--via', 'send-input')
    if ($Target) { $arguments += @('--target', $Target) }
    Ui $arguments | Out-Null
}

function Guest([string] $Script) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    $output = @(& winapp target exec sandbox -- powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
    ($output -join [Environment]::NewLine).Trim()
}

function Alpha([string] $Expected) {
    $value = Guest ('$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(''' + $fixture + '''); try { $key.GetValue(''Alpha'') } finally { $key.Dispose() }')
    if ($value -ne $Expected) { throw "Expected Alpha=$Expected, got $value." }
}

function Get-AlphaRow {
    $row = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Alpha, REG_SZ,*' })
    if ($row.Count -ne 1) { throw 'Expected exactly one Alpha registry row.' }
    $row[0]
}

function Open-Alpha {
    $row = Get-AlphaRow
    Ui @('invoke', $row.selector, '--action', 'select') | Out-Null
    Keys 'enter' $row.selector
    Ui @('wait-for', 'ValueDataEditor', '-t', '6000') | Out-Null
}

function Test-Ui([string] $Name, [scriptblock] $Check) {
    try {
        & $Check
        $results.Add(@{ name = $Name; status = 'PASS' })
        Write-Output "PASS: $Name"
    } catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_"; stack = $_.ScriptStackTrace })
        throw
    }
}

try {
    Keys 'escape'
    Guest ('$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(''' + $fixture + ''', $true); try { $key.SetValue(''Alpha'', ''original'') } finally { $key.Dispose() }') | Out-Null
    Keys 'f5' 'Registry keys and values'
    $initialToolbar = @(Read-Ui)
    $undoWidth = @($initialToolbar | Where-Object automationId -eq 'UndoButton')[0].width
    Test-Ui 'Redo is hidden when no action can be redone' {
        Wait-Ui { param($items) @($items | Where-Object automationId -eq 'RedoButton').Count -eq 0 }
    }

    Test-Ui 'Value editor keeps native text Undo before committing an edit' {
        Open-Alpha
        Keys 'ctrl+a' 'ValueDataEditor'
        Keys 'draft'
        Keys 'ctrl+z'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ValueDataEditor' -and $_.value -eq 'original' }).Count -eq 1 }
        Alpha 'original'
        Ui @('set-value', 'ValueDataEditor', 'intermediate') | Out-Null
        Ui @('invoke', 'PrimaryButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'UndoButton' -and $_.isEnabled }).Count -eq 1 }
        Alpha 'intermediate'
        Open-Alpha
        Ui @('set-value', 'ValueDataEditor', 'changed') | Out-Null
        Ui @('invoke', 'PrimaryButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'UndoButton' -and $_.isEnabled -and $_.name -eq 'Undo' }).Count -eq 1 }
        Alpha 'changed'
    }

    Test-Ui 'Empty modal text Undo never reaches committed registry history' {
        Open-Alpha
        Keys 'ctrl+a' 'ValueDataEditor'
        Keys 'draft'
        Keys 'ctrl+z'
        Keys 'ctrl+z'
        Keys 'ctrl+z'
        Alpha 'changed'
        Keys 'escape'
        Ui @('wait-for', 'PrimaryButton', '--gone', '-t', '6000') | Out-Null
    }

    Test-Ui 'Consecutive Undo and Redo shortcuts keep focus on the value row' {
        Ui @('focus', (Get-AlphaRow).selector) | Out-Null
        foreach ($step in @(
            @{ Keys = 'ctrl+z'; Value = 'intermediate'; ReadyButton = 'RedoButton' }
            @{ Keys = 'ctrl+z'; Value = 'original'; ReadyButton = 'RedoButton' }
            @{ Keys = 'ctrl+y'; Value = 'intermediate'; ReadyButton = 'UndoButton' }
            @{ Keys = 'ctrl+y'; Value = 'changed'; ReadyButton = 'UndoButton' }
        )) {
            Keys $step.Keys
            Wait-Ui {
                param($items)
                ($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq "Alpha, REG_SZ, $($step.Value)" }) -and
                    ($items | Where-Object { $_.automationId -eq $step.ReadyButton -and $_.isEnabled })
            }
            Alpha $step.Value
            $focused = Ui @('get-focused')
            if (!$focused.hasFocus -or $focused.element.name -ne "Alpha, REG_SZ, $($step.Value)") {
                throw "$($step.Keys) moved focus away from the value row."
            }
        }
    }

    Test-Ui 'Undoing the filter to empty keeps text focus and never undoes the registry' {
        Keys 'ctrl+alt+f' 'Registry keys and values'
        Keys 'Alpha' 'FilterBox'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FilterBox' -and $_.value -eq 'Alpha' }).Count -eq 1 }
        Keys 'ctrl+z'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FilterBox' -and [string]$_.value -eq '' }).Count -eq 1 }
        Keys 'ctrl+z'
        Keys 'ctrl+z'
        Alpha 'changed'
        Keys 'escape'
    }

    Test-Ui 'Address text Undo never reaches the registry history' {
        Keys 'ctrl+l' 'Registry keys and values'
        Keys 'temporary'
        Keys 'ctrl+z'
        Keys 'ctrl+z'
        Keys 'ctrl+z'
        Alpha 'changed'
        Keys 'escape'
    }

    Test-Ui 'New stays first and toolbar Undo and Redo execute their actions' {
        $items = @(Read-Ui)
        $new = @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'New' })
        $undo = @($items | Where-Object automationId -eq 'UndoButton')
        if ($new.Count -ne 1 -or $undo.Count -ne 1 -or $new[0].x -ge $undo[0].x) { throw 'New must precede Undo on the toolbar.' }
        if ($undo[0].name -ne 'Undo' -or $undo[0].width -ne $undoWidth) {
            throw 'Undo must keep its toolbar label and width after edits.'
        }
        $help = Ui @('get-property', 'UndoButton', '--property', 'HelpText') | ConvertTo-Json -Depth 10
        if ($help -notlike '*Alpha*' -or $help -notlike ('*' + $FixtureAddress.Replace('\', '\\') + '*')) {
            throw 'Undo help must retain the action and full target.'
        }
        Ui @('invoke', 'UndoButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'RedoButton' -and $_.isEnabled }).Count -eq 1 }
        Alpha 'intermediate'
        Ui @('invoke', 'RedoButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'UndoButton' -and $_.isEnabled }).Count -eq 1 }
        Alpha 'changed'
        Wait-Ui { param($items) @($items | Where-Object automationId -eq 'RedoButton').Count -eq 0 }
    }

    Test-Ui 'A second tab has no access to the first tabs edit history' {
        $tabCount = @(Read-Ui | Where-Object type -eq 'TabItem').Count
        Keys 'ctrl+t' 'Registry keys and values'
        Wait-Ui { param($items) @($items | Where-Object type -eq 'TabItem').Count -eq $tabCount + 1 }
        $tab = @(Read-Ui | Where-Object type -eq 'TabItem' | Sort-Object x)[-1]
        Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'UndoButton' -and -not $_.isEnabled }).Count -eq 1 }
        Keys 'ctrl+z' 'Registry keys and values'
        Alpha 'changed'
        Keys 'ctrl+w' 'Registry keys and values'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'UndoButton' -and $_.isEnabled -and $_.name -eq 'Undo' }).Count -eq 1 }
    }

    Test-Ui 'A conflict opens the exact Journal entry without overwriting foreign data' {
        Guest ('$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(''' + $fixture + ''', $true); try { $key.SetValue(''Alpha'', ''foreign'') } finally { $key.Dispose() }') | Out-Null
        Keys 'ctrl+z' 'Registry keys and values'
        Ui @('wait-for', 'Show in Journal', '-t', '6000') | Out-Null
        Alpha 'foreign'
        Ui @('invoke', 'Show in Journal', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'List' -and $_.name -eq 'Tool items' }).Count -eq 1 }
        $journal = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Alpha*' })
        if ($journal.Count -lt 2) { throw 'The selected edit was not revealed in Journal.' }
        $list = @(Read-Ui | Where-Object { $_.type -eq 'List' -and $_.name -eq 'Tool items' })
        if ($list.Count -ne 1) { throw 'Expected exactly one Journal list.' }
        Keys 'ctrl+z' 'Tool items'
        Alpha 'foreign'
    }

    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'undo-redo.png')) | Out-Null
} finally {
    $results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
    & winapp ui yield --on sandbox | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
    $env:WINAPP_EXPECT_SANDBOX = $previousSandbox
}
