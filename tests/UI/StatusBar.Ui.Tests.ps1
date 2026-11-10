param(
    [Parameter(Mandatory)][int] $AppPid,
    [Parameter(Mandatory)][string] $ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
Assert-UiTestProcess $AppPid
Assert-UiTestPath $ArtifactDirectory
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()

function Invoke-Ui([string[]] $Arguments, [long] $Window = $script:mainWindow) {
    $output = @(& winapp ui @Arguments -w $Window --on local 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui failed: $($output -join [Environment]::NewLine)" }
    $output
}

function Get-Elements($Nodes) {
    foreach ($node in $Nodes) {
        $node
        if ($node.children) { Get-Elements $node.children }
    }
}

function Read-Ui([long] $Window = $script:mainWindow) {
    $json = Invoke-Ui @('inspect', '-d', '14', '--hide-offscreen', '--json') -Window $Window
    $json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Encoding utf8
    @(Get-Elements (($json | ConvertFrom-Json).windows[0].elements))
}

function Wait-State([scriptblock] $Check) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Check @(Read-Ui)) { return }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw 'Expected UI state did not appear. See latest.json.'
}

function Read-Popup {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(& winapp ui list-windows -a $AppPid --on local --json | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0) { throw 'Could not list popup windows.' }
        $popup = @($windows | Where-Object { $_.label -eq 'popup' -and $_.ownerHwnd -eq $script:mainWindow })
        if ($popup.Count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    if ($popup.Count -ne 1) { throw 'Expected one status flyout.' }
    @{ window = $popup[0].hwnd; elements = @(Read-Ui -Window $popup[0].hwnd) }
}

function Test-StatusBar([string] $Name, [scriptblock] $Check) {
    & $Check
    $results.Add(@{ name = $Name; status = 'PASS' })
    Write-Output "PASS: $Name"
}

try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(& winapp ui list-windows -a $AppPid --on local --json | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0) { throw 'Could not find the test window.' }
        $main = @($windows | Where-Object { $_.className -eq 'WinUIDesktopWin32WindowClass' })
        if ($main.Count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 30)
    if ($main.Count -ne 1) { throw 'Expected one isolated test window.' }
    $script:mainWindow = $main[0].hwnd
    Wait-State { param($elements) $elements.automationId -contains 'RegistryViewStatus' }

    Test-StatusBar 'Status indicators are named buttons' {
        $elements = @(Read-Ui)
        foreach ($id in 'RegistryViewStatus', 'AccessStatus', 'ConnectionStatus') {
            $button = @($elements | Where-Object automationId -eq $id)
            if ($button.Count -ne 1 -or $button[0].type -ne 'Button' -or -not $button[0].name) {
                throw "Missing named status button: $id"
            }
        }
        if (($elements | Where-Object automationId -eq 'ConnectionStatus').name -ne 'This computer') {
            throw 'Local connection label is incorrect.'
        }
        Invoke-Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'status-bar.png')) | Out-Null
    }

    Test-StatusBar 'Registry view menu switches both ways' {
        foreach ($view in '32-bit registry', '64-bit registry') {
            Invoke-Ui @('invoke', 'RegistryViewStatus', '--action', 'invoke') | Out-Null
            $popup = Read-Popup
            $item = @($popup.elements | Where-Object { $_.type -eq 'MenuItem' -and $_.name -eq $view })
            if ($item.Count -ne 1) { throw "Missing view: $view" }
            Invoke-Ui @('invoke', $item[0].selector, '--action', 'toggle') -Window $popup.window | Out-Null
            Wait-State { param($elements) ($elements | Where-Object automationId -eq 'RegistryViewStatus').name -eq $view }
        }
    }

    Test-StatusBar 'Access button asks before starting another window' {
        $access = (Read-Ui | Where-Object automationId -eq 'AccessStatus').name
        Invoke-Ui @('invoke', 'AccessStatus', '--action', 'invoke') | Out-Null
        $popup = Read-Popup
        $expected = if ($access -eq 'Administrator') { 'Open with standard access' } else { 'Open as administrator' }
        if (($popup.elements | Where-Object automationId -eq 'AccessModeLaunchButton').name -ne $expected) {
            throw 'Access flyout does not offer the other mode.'
        }
        if (-not ($popup.elements.name -match 'current window will stay open')) { throw 'Access confirmation is missing.' }
        Invoke-Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'access-flyout.png')) -Window $popup.window | Out-Null
        Invoke-Ui @('send-keys', 'Escape', '--via', 'send-input') -Window $popup.window | Out-Null
    }

    Test-StatusBar 'Connection button opens the existing network dialog' {
        Invoke-Ui @('invoke', 'ConnectionStatus', '--action', 'invoke') | Out-Null
        Wait-State { param($elements) $elements.name -contains 'Connect network registry' }
        $cancel = @(Read-Ui | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Cancel' })
        if ($cancel.Count -ne 1) { throw 'Missing connection dialog cancel button.' }
        Invoke-Ui @('invoke', $cancel[0].selector, '--action', 'invoke') | Out-Null
    }
} catch {
    $results.Add(@{ name = 'Status bar UI'; status = 'FAIL'; detail = "$_" })
    throw
} finally {
    try { & winapp ui yield --on local | Out-Null }
    finally {
        $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
        @{ results = $results.ToArray() } | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
    }
}
