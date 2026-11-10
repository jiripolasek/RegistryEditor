param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string] $RegistryPath = 'HKCU'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    dotnet build src/Regedit/Regedit.csproj -c $Configuration -p:Platform=x64 -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    $executable = Join-Path $repositoryRoot "src/Regedit/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/regedit2.exe"
    & $executable $RegistryPath
}
finally {
    Pop-Location
}
