param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int] $AppPid,
    [Parameter(Mandatory)][string] $FixtureAddress,
    [Parameter(Mandatory)][string] $ArtifactDirectory,
    [Parameter(Mandatory)][string] $WorkflowId
)

$ErrorActionPreference = 'Stop'
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = $WorkflowId
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()

function Ui([string[]] $Arguments) {
    $output = @(& winapp ui @Arguments --on sandbox -a $AppPid --json 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
    ($output -join [Environment]::NewLine) | ConvertFrom-Json
}

function Elements($Nodes) {
    foreach ($node in $Nodes) {
        $node
        if ($node.children) { Elements $node.children }
    }
}

function Read-Ui {
    $tree = Ui @('inspect', '--depth', '18', '--hide-offscreen')
    @(Elements @($tree.windows | ForEach-Object { $_.elements }))
}

function Keys([string] $Value) { Ui @('send-keys', $Value, '--via', 'send-input') | Out-Null }
function Status([string] $Value) { Ui @('wait-for', 'BrowserStatusText', '--value', $Value, '--contains', '-t', '5000') | Out-Null }

function Selected-Result([string] $Name) {
    $elements = Read-Ui
    $row = @($elements | Where-Object {
        $_.type -eq 'ListItem' -and $_.name.StartsWith($Name + ', ' + $FixtureAddress)
    })
    if ($row.Count -ne 1) { throw "Expected one visible result row for $Name; found $($row.Count)." }
    Ui @('wait-for', $row[0].selector, '-p', 'IsSelected', '--value', 'True', '-t', '2000') | Out-Null
    $value = @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name.StartsWith($Name + ', REG_SZ,') })
    if ($value.Count -ne 1) { throw "Expected the current browser to display $Name." }
    Ui @('wait-for', $value[0].selector, '-p', 'IsSelected', '--value', 'True', '-t', '2000') | Out-Null
}

function Assert-Panel([bool] $Visible) {
    $shown = @(Read-Ui | Where-Object { $_.automationId -eq 'BottomTabs' }).Count -eq 1
    if ($shown -ne $Visible) { throw "Unexpected bottom panel visibility: $shown." }
}

function Assert-NoNotice {
    if (@(Read-Ui | Where-Object { $_.automationId -like 'Notification_*' }).Count -gt 0) {
        throw 'Routine F3 navigation opened the browser InfoBar.'
    }
}

function Save-Screenshot([string] $Name) {
    $path = Join-Path $ArtifactDirectory $Name
    Ui @('hover', 'BrowserStatusText') | Out-Null
    Ui @('screenshot', '--output', $path) | Out-Null
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Screenshot was not delivered.' }
    $screenshots.Add($path)
}

function Test-Ui([string] $Name, [scriptblock] $Check) {
    try {
        & $Check
        $results.Add(@{ name = $Name; status = 'PASS' })
        Write-Host "PASS: $Name"
    } catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_"; stack = $_.ScriptStackTrace })
        throw
    }
}

try {
    Test-Ui 'F3 selects the first result in the panel and browser' {
        Keys 'f3'
        Status 'Find: redneedle · 1/3'
        Selected-Result 'redneedleAlpha'
        Assert-Panel $true
        Assert-NoNotice
        Save-Screenshot '01-first-result.png'
    }
    Test-Ui 'F3 keeps the bottom panel hidden' {
        Keys 'ctrl+j'
        Assert-Panel $false
        Keys 'f3'
        Status 'Find: redneedle · 2/3'
        Assert-Panel $false
        Assert-NoNotice
    }
    Test-Ui 'A new browser tab continues the active result set' {
        Keys 'ctrl+t'
        Keys 'f3'
        Status 'Find: redneedle · 3/3'
        Assert-Panel $false
        $tabs = @(Read-Ui | Where-Object { $_.type -eq 'TabItem' })
        if ($tabs.Count -ne 2) { throw 'F3 opened an unexpected browser tab.' }
        Save-Screenshot '02-new-tab-hidden-panel.png'
    }
    Test-Ui 'Reopening the panel reveals its updated selection' {
        Keys 'ctrl+j'
        Selected-Result 'redneedleGamma'
        Keys 'f3'
        Status 'No more results.'
        Assert-Panel $true
        Assert-NoNotice
    }
    Test-Ui 'A second result set becomes the F3 search context' {
        Ui @('invoke', 'EditAddressButton') | Out-Null
        Ui @('wait-for', 'Address', '--type', 'Edit', '-t', '3000') | Out-Null
        Ui @('focus', 'Address', '--type', 'Edit') | Out-Null
        Ui @('wait-for', 'Address', '--type', 'Edit', '-p', 'HasKeyboardFocus', '--value', 'True', '-t', '3000') | Out-Null
        Ui @('set-value', 'Address', $FixtureAddress, '--type', 'Edit') | Out-Null
        Keys 'enter'
        Status '3 keys'
        Ui @('set-value', 'Search registry', 'blueneedle', '--type', 'Edit') | Out-Null
        Ui @('invoke', 'Start search') | Out-Null
        Ui @('wait-for', 'Find: blueneedle', '--type', 'TabItem', '-t', '5000') | Out-Null
        Keys 'f3'
        Status 'Find: blueneedle · 1/3'
        Selected-Result 'blueneedleAlpha'
    }
    Test-Ui 'F3 follows a result set selected by the user' {
        Ui @('invoke', 'Find: redneedle', '--type', 'TabItem') | Out-Null
        $row = Read-Ui | Where-Object {
            $_.type -eq 'ListItem' -and $_.name.StartsWith('redneedleAlpha, ' + $FixtureAddress)
        } | Select-Object -First 1
        if (-not $row) { throw 'The first red result is missing.' }
        Ui @('invoke', $row.selector) | Out-Null
        Keys 'f3'
        Status 'Find: redneedle · 2/3'
        Selected-Result 'redneedleBeta'
        Save-Screenshot '03-switched-result-set.png'
    }
    Test-Ui 'F3 works with keyboard focus in the result list' {
        $row = Read-Ui | Where-Object {
            $_.type -eq 'ListItem' -and $_.name.StartsWith('redneedleBeta, ' + $FixtureAddress)
        } | Select-Object -First 1
        Ui @('focus', $row.selector) | Out-Null
        Keys 'f3'
        Status 'Find: redneedle · 3/3'
        Selected-Result 'redneedleGamma'
    }
    Test-Ui 'F3 follows the displayed filter' {
        Ui @('set-value', 'SearchResultFilter', 'Alpha') | Out-Null
        Keys 'f3'
        Status 'Find: redneedle · 1/1'
        Selected-Result 'redneedleAlpha'
        Assert-NoNotice
    }
    Test-Ui 'Switching browser tabs retains the active search' {
        Ui @('set-value', 'SearchResultFilter', '') | Out-Null
        Keys 'ctrl+j'
        Keys 'ctrl+shift+tab'
        Keys 'f3'
        Status 'Find: redneedle · 2/3'
        Assert-Panel $false
        Assert-NoNotice
        Save-Screenshot '04-switched-browser-hidden-panel.png'
    }
} catch {
    Write-Host "FAIL: $_"
} finally {
    try { & winapp ui yield --on sandbox --json | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    $failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
    $report = [ordered]@{ target = 'sandbox'; appPid = $AppPid; passed = $results.Count - $failed; failed = $failed; results = @($results.ToArray()); screenshots = @($screenshots.ToArray()) }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
}
if ($failed -gt 0) { exit 1 }
