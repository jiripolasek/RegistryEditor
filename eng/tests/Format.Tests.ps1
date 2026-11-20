#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Regedit format fixture ' + [guid]::NewGuid().ToString('N'))
$oldLog = $env:REGEDIT_FORMAT_TEST_LOG
$checks = 0

function Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Output "PASS: $Message"
}

function Write-Fixture([string] $Path, [string] $Content = 'fixture') {
    $target = Join-Path $fixture $Path
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $Content)
}

function Invoke-FixtureGit([string[]] $Arguments) {
    & git.exe -C $fixture -c core.fsmonitor=false -c core.autocrlf=false -c commit.gpgsign=false @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git command failed: $Arguments" }
}

function Read-Calls {
    @(Get-Content -LiteralPath $env:REGEDIT_FORMAT_TEST_LOG -ErrorAction SilentlyContinue | ConvertFrom-Json)
}

# Capture exact argument boundaries without running formatters against the fixture's fake projects.
function dotnet {
    @{ Tool = 'dotnet'; Arguments = @($args) } | ConvertTo-Json -Compress |
        Add-Content -LiteralPath $env:REGEDIT_FORMAT_TEST_LOG
    $global:LASTEXITCODE = 0
}

function Get-Command($Name, $CommandType, $ErrorAction) {
    if ($Name -eq 'jb') { return [pscustomobject]@{ Source = (Join-Path $fixture 'jb.ps1') } }
    Microsoft.PowerShell.Core\Get-Command -Name $Name -CommandType $CommandType -ErrorAction $ErrorAction
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'eng') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'eng/Format.ps1'),
        (Join-Path $repositoryRoot 'eng/Format-Modified.ps1'),
        (Join-Path $repositoryRoot 'eng/Format.DotSettings') -Destination (Join-Path $fixture 'eng')
    $env:REGEDIT_FORMAT_TEST_LOG = Join-Path $fixture 'calls.jsonl'
    Write-Fixture 'jb.ps1' @'
@{ Tool = 'jb'; Arguments = @($args) } | ConvertTo-Json -Compress |
    Add-Content -LiteralPath $env:REGEDIT_FORMAT_TEST_LOG
$global:LASTEXITCODE = 0
'@
    Write-Fixture 'JPSoftworks.Regedit.slnx' '<Solution><Project Path="src/Core/Core.csproj"/><Project Path="src/App/App.csproj"/><Project Path="tests/Tests/Tests.csproj"/></Solution>'
    Write-Fixture 'src/Core/Core.csproj' '<Project/>'
    Write-Fixture 'src/App/App.csproj' '<Project/>'
    Write-Fixture 'tests/Tests/Tests.csproj' '<Project/>'
    foreach ($path in @('src/Core/Working Ω.cs', 'src/Core/Staged.cs', 'src/Core/Deleted.cs',
            'src/App/Old.cs', 'src/App/Clean.cs', 'src/App/Working page.xaml', 'src/App/Clean.xaml')) {
        Write-Fixture $path $path
    }
    Invoke-FixtureGit @('init', '-q')
    Invoke-FixtureGit @('add', '.')
    Invoke-FixtureGit @('-c', 'user.name=Formatter Test', '-c', 'user.email=formatter@example.invalid', 'commit', '-qm', 'Seed fixture')

    $before = Get-Location
    & (Join-Path $fixture 'eng/Format-Modified.ps1')
    Check ((Read-Calls).Count -eq 0) 'A clean worktree runs no formatters'
    Check ((Get-Location).Path -eq $before.Path) 'The caller location is preserved'

    Write-Fixture 'src/Core/Working Ω.cs' 'working change'
    Write-Fixture 'src/Core/Staged.cs' 'staged change'
    Write-Fixture 'src/Core/Added.cs' 'staged addition'
    Write-Fixture 'src/App/Working page.xaml' 'working XAML'
    Write-Fixture 'src/App/Added page.xaml' 'staged XAML'
    Write-Fixture 'src/App/Untracked Ω.cs' 'untracked'
    Write-Fixture 'src/App/Untracked page.xaml' 'untracked XAML'
    Write-Fixture '.gitignore' 'src/App/Ignored*'
    Write-Fixture 'src/App/Ignored.cs' 'ignored'
    Write-Fixture 'src/App/Ignored.xaml' 'ignored XAML'
    Write-Fixture 'src/App/bin/Untracked.cs' 'generated'
    Write-Fixture 'src/App/obj/Generated.cs' 'generated'
    Invoke-FixtureGit @('add', 'src/Core/Staged.cs', 'src/Core/Added.cs', 'src/App/Added page.xaml', 'src/App/obj/Generated.cs')
    Invoke-FixtureGit @('mv', 'src/App/Old.cs', 'src/App/Renamed.cs')
    Invoke-FixtureGit @('rm', '-q', 'src/Core/Deleted.cs')
    $indexBefore = & git.exe -C $fixture -c core.fsmonitor=false ls-files --stage
    & (Join-Path $fixture 'eng/Format-Modified.ps1')
    $calls = Read-Calls
    $roslyn = @($calls | Where-Object { $_.Tool -eq 'dotnet' -and $_.Arguments[0] -eq 'format' })
    $actual = @($roslyn | ForEach-Object {
        $at = [array]::IndexOf($_.Arguments, '--include')
        $_.Arguments[($at + 1)..($_.Arguments.Count - 1)] | ForEach-Object {
            [IO.Path]::GetRelativePath($fixture, $_).Replace('\', '/')
        }
    } | Sort-Object)
    $expected = @('src/App/Renamed.cs', 'src/App/Untracked Ω.cs', 'src/Core/Added.cs', 'src/Core/Staged.cs', 'src/Core/Working Ω.cs')
    Check (($actual -join '|') -ceq ($expected -join '|')) 'Roslyn includes new, modified, staged and renamed C# files while excluding ignored and generated files'
    Check ($roslyn.Count -eq 2 -and @($roslyn | Where-Object { $_.Arguments[1] -eq 'tests/Tests/Tests.csproj' }).Count -eq 0) 'Linked files are formatted only through their owning project'
    $cleanup = @($calls | Where-Object Tool -eq 'jb')
    $mask = @($cleanup[0].Arguments | Where-Object { $_.StartsWith('--include=') })[0].Substring(10)
    Check ($cleanup.Count -eq 1 -and (($mask -split ';' | Sort-Object) -join '|') -ceq ($expected -join '|')) 'ReSharper receives exactly the changed C# scope'
    $styler = @($calls | Where-Object { $_.Tool -eq 'dotnet' -and $_.Arguments[0] -eq 'tool' -and $_.Arguments[1] -eq 'run' })
    $xaml = @($styler | ForEach-Object {
        $at = [array]::IndexOf($_.Arguments, '--file')
        [IO.Path]::GetRelativePath($fixture, $_.Arguments[$at + 1]).Replace('\', '/')
    } | Sort-Object)
    Check (($xaml -join '|') -ceq 'src/App/Added page.xaml|src/App/Untracked page.xaml|src/App/Working page.xaml') 'XAML Styler includes untracked files without ignored, generated or clean neighbors'
    Check (($indexBefore -join "`n") -ceq ((& git.exe -C $fixture -c core.fsmonitor=false ls-files --stage) -join "`n")) 'Formatting leaves staged content unchanged'

    $count = $calls.Count
    & (Join-Path $fixture 'eng/Format.ps1') -Include @()
    Check ((Read-Calls).Count -eq $count) 'An explicitly empty include set cannot trigger full-solution formatting'
    & (Join-Path $fixture 'eng/Format.ps1') -Include 'src/App/Working page.xaml'
    Check (@((Read-Calls) | Where-Object Tool -eq 'jb').Count -eq 1) 'XAML-only formatting skips ReSharper'
    foreach ($path in @('src/App/Unsafe;Name.cs', 'src/App/Unsafe,Name.xaml')) {
        Write-Fixture $path
        $count = (Read-Calls).Count
        $rejected = $false
        try { & (Join-Path $fixture 'eng/Format.ps1') -Include $path }
        catch { $rejected = $_.Exception.Message -like '*cannot select this filename literally*' }
        Check ($rejected -and (Read-Calls).Count -eq $count) "Formatter separators cannot widen the selected scope: $path"
    }
    $count = (Read-Calls).Count
    & (Join-Path $fixture 'eng/Format.ps1')
    $full = @((Read-Calls) | Select-Object -Skip $count)
    Check (@($full | Where-Object { $_.Tool -eq 'dotnet' -and $_.Arguments[0] -eq 'format' }).Count -eq 3 -and
        @($full | Where-Object { $_.Tool -eq 'jb' -and $_.Arguments -contains '--include=src/**/*.cs;tests/**/*.cs' }).Count -eq 1) 'The original full-solution entry point retains its complete C# scope'
    [xml]$settings = Get-Content -LiteralPath (Join-Path $fixture 'eng/Format.DotSettings') -Raw
    [xml]$profile = $settings.SelectSingleNode("//*[local-name()='String']").InnerText
    Check ($profile.Profile.CSReorderTypeMembers -eq 'True') 'The shared cleanup profile enables Apply file layout'
    Write-Output "$checks formatter checks passed."
}
finally {
    $env:REGEDIT_FORMAT_TEST_LOG = $oldLog
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'Regedit format fixture *') { throw 'Refusing to remove an unexpected fixture path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
