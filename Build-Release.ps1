$ErrorActionPreference = 'Stop'
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$releaseRoot = Join-Path $artifactsRoot 'release'

foreach ($outputPath in @($artifactsRoot, $releaseRoot)) {
    if (Test-Path -LiteralPath $outputPath) {
        $entry = Get-Item -LiteralPath $outputPath -Force
        if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Release output must be a regular directory: $outputPath"
        }
    }
}
if ([IO.Path]::GetDirectoryName($releaseRoot) -ne $artifactsRoot) {
    throw 'Release output must be inside the artifacts directory.'
}
if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

Push-Location $PSScriptRoot
try {
    foreach ($platform in @('x64', 'ARM64')) {
        $runtime = 'win-' + $platform.ToLowerInvariant()
        $publishDirectory = Join-Path $releaseRoot "$runtime/publish"
        $symbolsDirectory = Join-Path $releaseRoot "$runtime/symbols"
        dotnet publish src/Regedit/Regedit.csproj -c Release "-p:Platform=$platform" "-p:PublishProfile=$runtime" -o $publishDirectory -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) {
            throw "Publishing $runtime failed (exit $LASTEXITCODE)."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'regedit2.exe')) -or
            -not (Test-Path -LiteralPath (Join-Path $symbolsDirectory 'regedit2.pdb')) -or
            (Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File -Recurse)) {
            throw "Publishing $runtime did not produce a complete app with separate symbols."
        }
        $versionInfo = (Get-Item -LiteralPath (Join-Path $publishDirectory 'regedit2.exe')).VersionInfo
        foreach ($metadataField in @('CompanyName', 'FileDescription', 'FileVersion', 'LegalCopyright', 'ProductName', 'ProductVersion')) {
            if ([string]::IsNullOrWhiteSpace($versionInfo.$metadataField)) {
                throw "Missing $metadataField in $runtime release metadata."
            }
        }
        Copy-Item -LiteralPath LICENSE.txt, NOTICE.md -Destination $publishDirectory
        Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath (Join-Path $releaseRoot "Regedit-$runtime.zip")
        Compress-Archive -Path (Join-Path $symbolsDirectory '*') -DestinationPath (Join-Path $releaseRoot "Regedit-$runtime-symbols.zip")
    }
    Write-Host "Release artifacts: $releaseRoot"
}
finally {
    Pop-Location
}
