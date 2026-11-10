#Requires -Version 7.0
param(
    [ValidateSet('Fast', 'Edits', 'Registry', 'Ui', 'NativeUi', 'NoteClose', 'Launch', 'TreeActions')]
    [string] $Suite = 'Edits',
    [string] $TestFilter,
    [string[]] $ManagedSuite = @(),
    [switch] $ListManagedSuites,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'artifacts/sandbox'
$inputDir = Join-Path $work 'input'
$resultsDir = Join-Path $work 'results'
$timer = [Diagnostics.Stopwatch]::StartNew()
$null = New-Item -ItemType Directory -Force -Path $inputDir, $resultsDir

function Invoke-Wsb([string[]] $Arguments) {
    $reply = & wsb.exe @Arguments --raw 2>&1
    if ($LASTEXITCODE -ne 0) { throw "wsb $($Arguments[0]) failed. $($reply -join ' ')" }
    if ($reply) { ($reply -join [Environment]::NewLine) | ConvertFrom-Json }
}

function Connect-SandboxDesktop([string] $Id) {
    $probe = @('exec', '--id', $id, '--run-as', 'ExistingLogin', '--command', 'cmd.exe /d /c exit 0', '--raw')
    $null = & wsb.exe @probe 2>&1
    if ($LASTEXITCODE -eq 0) { return $false }
    Start-Process -FilePath 'wsb.exe' -ArgumentList @('connect', '--id', $id) -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $reply = & wsb.exe @probe 2>&1
    } while ($LASTEXITCODE -ne 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($LASTEXITCODE -ne 0) { throw "The signed-in Sandbox desktop is unavailable. $reply" }
    return $true
}

# A desktop has one input focus, and a running guest may still be reading the build.
$lock = [IO.File]::Open((Join-Path $work 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $run = [guid]::NewGuid().ToString('N')
    $output = New-Item -ItemType Directory -Path (Join-Path $resultsDir $run)
    $isUi = $Suite -in 'Ui', 'NativeUi', 'NoteClose', 'Launch', 'TreeActions'
    if ($isUi -and ($ManagedSuite.Count -gt 0 -or $ListManagedSuites)) {
        throw 'ManagedSuite and ListManagedSuites apply only to Fast, Edits and Registry.'
    }
    if (-not $TestFilter) { $TestFilter = if ($Suite -in 'Ui', 'NativeUi') { 'Tree Delete and F2|Empty list selection' } else { '.' } }
    $scripts = @("$root/tests/UI/Invoke-SandboxChecks.ps1")
    if ($isUi) {
        $null = [regex]::new($TestFilter)
        $uiScript = if ($Suite -in 'Ui', 'NativeUi') { 'Investigations' } else { $Suite }
        $scripts += "$root/tests/UI/$uiScript.Ui.Tests.ps1", "$root/tests/UI/UiTestWindow.ps1", "$root/tests/UI/UiTestProfile.ps1"
        if ($Suite -in 'Ui', 'NativeUi' -and 'Window chrome retains menu tab and layout behavior' -match $TestFilter) {
            $scripts += "$root/tests/UI/Layout.Ui.Tests.ps1", "$root/tests/UI/Shell.Ui.Tests.ps1"
        }
    }
    foreach ($scriptPath in $scripts) {
        $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
        if ($parseErrors.Count) { throw "$scriptPath $($parseErrors[0])" }
    }
    if (-not $NoBuild) {
        if ($Suite -eq 'NativeUi') {
            dotnet publish "$root/src/Regedit/Regedit.csproj" -c Release -r win-x64 -p:Platform=x64 -p:PublishAot=true -o "$inputDir/builds/ui-aot" -m:1 -nr:false
        }
        elseif ($isUi) {
            dotnet build "$root/src/Regedit/Regedit.csproj" -c Debug -p:Platform=x64 -p:RegeditUiTests=true -p:IntermediateOutputPath=obj\SandboxUi\ "-p:OutDir=$inputDir\builds\ui" -m:1 -nr:false
        }
        else {
            dotnet publish "$root/tests/Regedit.Tests/Regedit.Tests.csproj" -c Debug -r win-x64 --self-contained true -o "$inputDir/builds/registry" -m:1 -nr:false
        }
        if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    }
    $executable = if ($Suite -eq 'NativeUi') { 'builds/ui-aot/regedit2.exe' } elseif ($isUi) { 'builds/ui/regedit2.exe' } else { 'builds/registry/Regedit.Tests.exe' }
    $executablePath = Join-Path $inputDir $executable
    if (-not (Test-Path -LiteralPath $executablePath)) { throw 'Run once without -NoBuild to build this suite.' }
    if ($isUi) {
        . "$root/tests/UI/UiTestProfile.ps1"
        if ($Suite -eq 'NativeUi') { Assert-ShippingUiBuild $executablePath } else { Assert-UiTestBuild $executablePath }
    }
    Copy-Item -LiteralPath $scripts -Destination $inputDir
    if ($isUi) {
        $winapp = Get-Item -LiteralPath (Get-Command winapp.exe).Source
        if ($winapp.Length -eq 0) {
            $winapp = Get-Item -LiteralPath (Join-Path (Get-AppxPackage -Name winapp).InstallLocation 'winapp.exe')
        }
        & robocopy $winapp.DirectoryName "$inputDir/winapp" winapp.exe '*.dll' /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Could not stage WinApp CLI for the guest.' }
    }

    $guests = @( (Invoke-Wsb @('list')).WindowsSandboxEnvironments | Where-Object Id )
    if ($guests.Count -eq 0) {
        $null = Invoke-Wsb @('start', '--config', '<Configuration><ClipboardRedirection>Disable</ClipboardRedirection><Networking>Disable</Networking></Configuration>')
        $guests = @( (Invoke-Wsb @('list')).WindowsSandboxEnvironments | Where-Object Id )
    }
    if ($guests.Count -ne 1) { throw "Expected one Windows Sandbox instance, found $($guests.Count)." }
    $id = $guests[0].Id
    $cache = Join-Path $work 'session.json'
    $session = if (Test-Path -LiteralPath $cache) { Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json }
    if ($session.Id -ne $id) {
        $base = 'C:\Regedit-' + [guid]::NewGuid().ToString('N')
        $null = Connect-SandboxDesktop $id
        $shellDir = Join-Path $env:ProgramFiles 'PowerShell/7'
        if (-not (Test-Path -LiteralPath "$shellDir/pwsh.exe")) { $shellDir = $PSHOME }
        $null = Invoke-Wsb @('share', '--id', $id, '-f', $inputDir, '-s', "$base-input")
        $null = Invoke-Wsb @('share', '--id', $id, '-f', $shellDir, '-s', "$base-shell")
        $null = Invoke-Wsb @('share', '--id', $id, '-f', $resultsDir, '-s', "$base-results", '--allow-write')
        $session = @{ Id = $id; Base = $base }
        $session | ConvertTo-Json | Set-Content -LiteralPath $cache
    }
    $base = $session.Base
    @{ Suite = $Suite; Filter = $TestFilter; ManagedSuites = $ManagedSuite; ListManagedSuites = [bool]$ListManagedSuites } | ConvertTo-Json | Set-Content -LiteralPath "$output/request.json"
    $command = "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File $base-input\Invoke-SandboxChecks.ps1 -Base $base -Run $run"

    Write-Host "Windows Sandbox $id, $Suite. Results in $output"
    $guestTime = [Diagnostics.Stopwatch]::StartNew()
    $execute = @('exec', '--id', $id, '--run-as', 'ExistingLogin', '--command', $command)
    try { $status = Invoke-Wsb $execute }
    catch {
        if ((Test-Path -LiteralPath "$output/guest.log") -or (Test-Path -LiteralPath "$output/ui-worker.pid") -or -not (Connect-SandboxDesktop $id)) { throw }
        $status = Invoke-Wsb $execute
    }
    $guestTime.Stop()
    $exitCode = $status.ExitCode
    if ($null -eq $exitCode -or -not (Test-Path -LiteralPath "$output/guest.log")) { $exitCode = 1 }
    if ($isUi -and $exitCode -eq 0) {
        $report = if (Test-Path -LiteralPath "$output/results.json") { Get-Content -LiteralPath "$output/results.json" -Raw | ConvertFrom-Json }
        if (-not $report.results -or @($report.results | Where-Object status -ne 'PASS').Count) { $exitCode = 1 }
    }
    @{
        sandbox = $id
        suite = $Suite
        guestSeconds = [Math]::Round($guestTime.Elapsed.TotalSeconds, 2)
        totalSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
        exitCode = $exitCode
    } | ConvertTo-Json | Set-Content -LiteralPath "$output/run.json"
    if (Test-Path -LiteralPath "$output/guest.log") { Get-Content -LiteralPath "$output/guest.log" -Tail 8 }
    Write-Host ('Guest {0:N2}s, total {1:N2}s.' -f $guestTime.Elapsed.TotalSeconds, $timer.Elapsed.TotalSeconds)
    if ($exitCode -ne 0) { throw "Sandbox $Suite failed (exit $exitCode). See $output." }
}
finally { $lock.Dispose() }
