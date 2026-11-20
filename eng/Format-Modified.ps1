<#
.SYNOPSIS
    Formats new, modified and staged added, copied, modified or renamed C# and XAML files.
.NOTES
    Formats working copies only. The Git index is not updated; stage the formatting afterward.
.EXAMPLE
    .\eng\Format-Modified.ps1
#>
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$files = @()
foreach ($cached in @($false, $true)) {
    $arguments = @('-C', $repositoryRoot, '-c', 'core.fsmonitor=false', 'diff', '--name-only', '--diff-filter=ACMR', '-z')
    if ($cached) { $arguments += '--cached' }
    $output = & git @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate changed files in Git.' }
    $files += ($output -join "`n") -split "`0" | Where-Object { $_.Length -gt 0 }
}
$output = & git -C $repositoryRoot -c core.fsmonitor=false ls-files --others --exclude-standard -z
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate new files in Git.' }
$files += ($output -join "`n") -split "`0" | Where-Object { $_.Length -gt 0 }
$files = @($files | Sort-Object -Unique)
& (Join-Path $PSScriptRoot 'Format.ps1') -Include $files
