<#
.SYNOPSIS
    Formats the solution's C# and XAML using .editorconfig and Settings.XamlStyler.
.EXAMPLE
    .\eng\Format.ps1
#>
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location -LiteralPath $repositoryRoot
try {
    $resharper = Get-Command jb -CommandType Application -ErrorAction SilentlyContinue
    if (-not $resharper) {
        throw "Install the ReSharper CLI with 'dotnet tool install --global JetBrains.ReSharper.GlobalTools --version 2025.3.3'."
    }

    dotnet tool restore --tool-manifest dotnet-tools.json
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore XAML Styler.' }

    # Format linked files in their owning project so Roslyn never merges competing fixes.
    $solution = [xml](Get-Content -Raw -LiteralPath 'JPSoftworks.Regedit.slnx')
    foreach ($project in $solution.SelectNodes('//Project')) {
        $directory = Split-Path -Parent $project.Path
        dotnet format $project.Path --include "$directory/**/*.cs"
        if ($LASTEXITCODE -ne 0) { throw "dotnet format failed for '$($project.Path)'." }
    }

    # ReSharper also qualifies event subscriptions and applies member spacing.
    & $resharper.Source cleanupcode JPSoftworks.Regedit.slnx --profile=Regedit --settings=eng/Format.DotSettings `
        '--include=src/**/*.cs;tests/**/*.cs' '--exclude=**/bin/**;**/obj/**;**/artifacts/**;**/TestResults/**' `
        --properties=Platform=x64 --no-build --no-updates --no-buildin-settings
    if ($LASTEXITCODE -ne 0) { throw 'ReSharper formatting failed.' }

    $xamlDirectories = Get-ChildItem -LiteralPath 'src', 'tests' -Filter '*.xaml' -File -Recurse |
        Where-Object { $_.FullName.Substring($repositoryRoot.Length) -notmatch '[\\/](bin|obj|artifacts|TestResults)[\\/]' } |
        Select-Object -ExpandProperty DirectoryName -Unique
    foreach ($directory in $xamlDirectories) {
        dotnet tool run xstyler -- --config Settings.XamlStyler --directory $directory
        if ($LASTEXITCODE -ne 0) { throw "XAML Styler failed in '$directory'." }
    }
}
finally {
    Pop-Location
}
