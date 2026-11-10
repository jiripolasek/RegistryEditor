param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string] $RegistryPath = 'HKCU'
)

& (Join-Path $PSScriptRoot 'eng/Run.ps1') @PSBoundParameters
