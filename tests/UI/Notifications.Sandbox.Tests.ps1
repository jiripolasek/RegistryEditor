param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ProfileRoot,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$WinApp,
    [Parameter(Mandatory)][guid]$SandboxRun,
    [string]$TestFilter = '.',
    [switch]$ExpectNative
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/UiTestProfile.ps1"
Assert-UiTestBuild $Executable
$previousRun = $env:REGEDIT_UI_TEST_SANDBOX_RUN
$previousData = $env:REGEDIT_UI_TEST_DATA
$env:REGEDIT_UI_TEST_SANDBOX_RUN = $SandboxRun.ToString('N')
$results = [Collections.Generic.List[object]]::new()
$cases = @(
    @{ Name = 'Templates and application styles'; Options = @{ TemplatesOnly = $true } },
    @{ Name = 'Layout and keyboard'; Options = @{ LayoutOnly = $true; Keyboard = $true } },
    @{ Name = 'Live history and neutral native icons'; Options = @{ ReviewFixesOnly = $true } },
    @{ Name = 'Middle-click dismissal'; Options = @{ MiddleClickOnly = $true } },
    @{ Name = 'Workflow and built-in themes'; Options = @{ Styles = $true } }
)
try {
    foreach ($case in @($cases | Where-Object Name -Match $TestFilter)) {
        $app = $null
        try {
            $env:REGEDIT_UI_TEST_DATA = New-UiTestProfile (Join-Path $ProfileRoot ([guid]::NewGuid().ToString('N')))
            $app = Start-Process -FilePath $Executable -ArgumentList '--notification-prototype' -PassThru
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            do {
                Start-Sleep -Milliseconds 150
                $app.Refresh()
            } while (-not $app.HasExited -and (-not $app.MainWindowHandle -or $app.MainWindowTitle -ne 'Notifications prototype') -and [DateTime]::UtcNow -lt $deadline)
            if ($app.HasExited -or -not $app.MainWindowHandle -or $app.MainWindowTitle -ne 'Notifications prototype') {
                throw 'The Sandbox playground did not open.'
            }
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
            $bounds = $window.Current.BoundingRectangle
            $point = @{ Pid = $app.Id; Hwnd = [long]$app.MainWindowHandle; X = [int]($bounds.Left + 60); Y = [int]($bounds.Top + 16) }
            $ack = Join-Path $ArtifactDirectory "activation-$($app.Id)-$($app.MainWindowHandle).exit"
            [IO.File]::WriteAllText("$ArtifactDirectory/activate.tmp", ($point | ConvertTo-Json -Compress))
            [IO.File]::Move("$ArtifactDirectory/activate.tmp", "$ArtifactDirectory/activate.json")
            if (-not [Threading.SpinWait]::SpinUntil([Func[bool]] { [IO.File]::Exists($ack) }, 15000) -or
                [IO.File]::ReadAllText($ack) -ne '0') { throw 'The Sandbox desktop could not activate the verified playground.' }
            if ($ExpectNative -and @($app.Modules | Where-Object ModuleName -EQ 'coreclr.dll').Count) {
                throw 'The Native AOT fixture loaded CoreCLR.'
            }
            Write-Output "Starting: $($case.Name). Native AOT: $ExpectNative"
            $options = $case.Options
            & "$PSScriptRoot/Notifications.Ui.Tests.ps1" -AppPid $app.Id -WinApp $WinApp -SandboxRun $SandboxRun -SourceRoot $SourceRoot -ArtifactDirectory $ArtifactDirectory @options
            $results.Add(@{ name = $case.Name; status = 'PASS' })
        }
        catch {
            if ($app) { Write-Output "Playground exited: $($app.HasExited)" }
            if (Test-Path -LiteralPath $env:REGEDIT_UI_TEST_DATA) {
                Get-ChildItem -LiteralPath $env:REGEDIT_UI_TEST_DATA -Filter '*.log' -File -Recurse | ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $ArtifactDirectory ('failure-' + $_.Name)) -Force
                }
            }
            $results.Add(@{ name = $case.Name; status = 'FAIL'; detail = $_.ToString() })
            throw
        }
        finally {
            if ($app -and -not $app.HasExited) { $null = $app.CloseMainWindow(); if (-not $app.WaitForExit(3000)) { $app.Kill() } }
        }
    }
    if (-not $results.Count) { throw 'The notification filter selected no checks.' }
}
finally {
    $env:REGEDIT_UI_TEST_SANDBOX_RUN = $previousRun
    $env:REGEDIT_UI_TEST_DATA = $previousData
    @{ results = $results.ToArray(); nativeAot = [bool]$ExpectNative } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
}
