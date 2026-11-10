param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string] $TestDataDirectory,
    [string] $ResultsFile
)

$ErrorActionPreference = 'Stop'
$results = [Collections.Generic.List[object]]::new()
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
Assert-UiTestBuild $Executable
if ($ResultsFile) { Assert-UiTestPath $ResultsFile }
$Executable = [IO.Path]::GetFullPath($Executable)
$profilePath = New-UiTestProfile $TestDataDirectory
$settingsPath = Join-Path $profilePath 'settings.json'

function Get-Elements($Nodes) {
    foreach ($node in $Nodes) {
        $node
        if ($node.children) { Get-Elements $node.children }
    }
}

function Test-Launch([string] $Case, [string[]] $Arguments, [string] $ExpectedAddress, [switch] $Notice, [string[]] $ExpectedTabs) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.Environment['REGEDIT_UI_TEST_DATA'] = $profilePath
    foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $process.Refresh()
            if ($process.HasExited) { throw "The test window exited during $Case." }
            $hwnd = $process.MainWindowHandle.ToInt64()
            if ($hwnd -ne 0) {
                $tree = & winapp ui inspect -w "$hwnd" -d 14 --json --on local
                if ($LASTEXITCODE -eq 0) {
                    $elements = @(Get-Elements (($tree | ConvertFrom-Json).windows[0].elements))
                    $expectedTitle = ($ExpectedAddress -split '\\')[-1]
                    if ($Notice -and $elements.name -contains 'Startup notice') { break }
                    if (-not $Notice -and $elements.automationId -contains 'EntriesList' -and
                        @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $expectedTitle }).Count) { break }
                }
            }
            Start-Sleep -Milliseconds 150
        } while ([DateTime]::UtcNow -lt $deadline)
        if ([DateTime]::UtcNow -ge $deadline) { throw "Expected UI did not appear for $Case." }
        if ($ExpectedTabs) {
            $tabs = @($elements | Where-Object type -eq TabItem | Sort-Object x | ForEach-Object name)
            if (($tabs -join ',') -ne ($ExpectedTabs -join ',')) { throw "Restored tab order was $tabs." }
        }
        if ($Notice) {
            $ok = @($elements | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'OK' })
            if ($ok.Count -ne 1) { throw 'Expected one OK button on the startup notice.' }
            & winapp ui invoke $ok[0].selector --action invoke -w "$hwnd" --on local | Out-Null
            if ($LASTEXITCODE) { throw 'Could not dismiss the startup notice.' }
        }
        if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) {
            throw "Window did not close cleanly for $Case."
        }
        $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($ExpectedAddress -notin @($saved.Tabs | ForEach-Object { $_.Address })) { throw "Session for $Case is missing $ExpectedAddress." }
        $logFiles = Get-ChildItem -LiteralPath (Join-Path $profilePath 'Logs') -Filter '*.log'
        foreach ($event in 'started', 'stopped') {
            $entries = @($logFiles | Select-String -SimpleMatch "[$($process.Id)] JPSoftworks.Regedit.App: Registry Editor $event")
            if ($entries.Count -ne 1) { throw "Expected one '$event' log entry for the $Case process." }
        }
        $results.Add(@{ name = $Case; status = 'PASS' })
        Write-Output "PASS: $Case"
    }
    finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}

Test-Launch 'Fresh launch and clean shutdown' @() 'HKEY_CURRENT_USER'
Test-Launch 'Explicit registry location' @('HKCU\Software') 'HKEY_CURRENT_USER\Software'
Test-Launch 'Restore saved registry locations' @() 'HKEY_CURRENT_USER\Software' -ExpectedTabs @('HKEY_CURRENT_USER', 'Software')
Test-Launch 'Malformed activation presents a startup notice' @('invalid-hive\key') 'Computer' -Notice

if ($ResultsFile) {
    @{ results = $results.ToArray() } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultsFile -Encoding utf8
}
