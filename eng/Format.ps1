<#
.SYNOPSIS
    Formats C# and XAML, including ReSharper file layout.
.PARAMETER Include
    Limits formatting to these repository-relative or absolute files.
.EXAMPLE
    .\eng\Format.ps1
#>
param(
    [AllowEmptyCollection()]
    [string[]] $Include
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$limited = $PSBoundParameters.ContainsKey('Include')
$files = @()
if ($limited) {
    $files = @(foreach ($path in $Include) {
        $inputPath = if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $repositoryRoot $path }
        $absolute = [IO.Path]::GetFullPath($inputPath)
        if (-not $absolute.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Formatting input is outside the repository: '$path'."
        }
        $relative = $absolute.Substring($repositoryRoot.Length + 1).Replace('\', '/')
        if ($relative -match '\.(cs|xaml)$' -and $relative -notmatch '(^|/)(bin|obj|artifacts|TestResults)/' -and
            [IO.File]::Exists($absolute)) {
            # These CLI options split their values, so reject names that would widen the selected scope.
            if (($relative -match '\.cs$' -and $relative.Contains(';')) -or
                ($relative -match '\.xaml$' -and $relative.Contains(','))) {
                throw "The formatter cannot select this filename literally: '$relative'."
            }
            $relative
        }
    })
    $files = @($files | Sort-Object -Unique)
    if ($files.Count -eq 0) {
        Write-Output 'No changed C# or XAML files to format.'
        return
    }
}
$csharp = @($files | Where-Object { $_ -match '\.cs$' })
$xaml = @($files | Where-Object { $_ -match '\.xaml$' })

Push-Location -LiteralPath $repositoryRoot
try {
    if (-not $limited -or $csharp.Count -gt 0) {
        $resharper = Get-Command jb -CommandType Application -ErrorAction SilentlyContinue
        if (-not $resharper) {
            throw "Install the ReSharper CLI with 'dotnet tool install --global JetBrains.ReSharper.GlobalTools --version 2025.3.3'."
        }

        # Format linked files in their owning project so Roslyn never merges competing fixes.
        $solution = [xml](Get-Content -Raw -LiteralPath 'JPSoftworks.Regedit.slnx')
        foreach ($project in $solution.SelectNodes('//Project')) {
            $directory = (Split-Path -Parent $project.Path).Replace('\', '/')
            $projectFiles = @("$directory/**/*.cs")
            if ($limited) {
                $projectFiles = @($csharp | Where-Object { $_.StartsWith($directory + '/', [StringComparison]::OrdinalIgnoreCase) } |
                    ForEach-Object { Join-Path $repositoryRoot $_ })
                if ($projectFiles.Count -eq 0) { continue }
            }
            dotnet format $project.Path --include @projectFiles
            if ($LASTEXITCODE -ne 0) { throw "dotnet format failed for '$($project.Path)'." }
        }

        # ReSharper applies file layout, qualifier arrangement and member spacing.
        $cleanupInclude = if ($limited) { $csharp -join ';' } else { 'src/**/*.cs;tests/**/*.cs' }
        & $resharper.Source cleanupcode JPSoftworks.Regedit.slnx --profile=Regedit --settings=eng/Format.DotSettings `
            "--include=$cleanupInclude" '--exclude=**/bin/**;**/obj/**;**/artifacts/**;**/TestResults/**' `
            --properties=Platform=x64 --no-build --no-updates --no-buildin-settings
        if ($LASTEXITCODE -ne 0) { throw 'ReSharper formatting failed.' }
    }

    if (-not $limited -or $xaml.Count -gt 0) {
        dotnet tool restore --tool-manifest dotnet-tools.json
        if ($LASTEXITCODE -ne 0) { throw 'Could not restore XAML Styler.' }
        if ($limited) {
            foreach ($file in $xaml) {
                dotnet tool run xstyler -- --config Settings.XamlStyler --file (Join-Path $repositoryRoot $file)
                if ($LASTEXITCODE -ne 0) { throw "XAML Styler failed for '$file'." }
            }
        }
        else {
            $xamlDirectories = Get-ChildItem -LiteralPath 'src', 'tests' -Filter '*.xaml' -File -Recurse |
                Where-Object { $_.FullName.Substring($repositoryRoot.Length) -notmatch '[\\/](bin|obj|artifacts|TestResults)[\\/]' } |
                Select-Object -ExpandProperty DirectoryName -Unique
            foreach ($directory in $xamlDirectories) {
                dotnet tool run xstyler -- --config Settings.XamlStyler --directory $directory
                if ($LASTEXITCODE -ne 0) { throw "XAML Styler failed in '$directory'." }
            }
        }
    }
}
finally {
    Pop-Location
}
