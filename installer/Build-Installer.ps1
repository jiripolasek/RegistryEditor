param(
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CertificateThumbprint,
    [switch] $MachineCertificateStore,
    [string] $TrustedSigningDll,
    [string] $TrustedSigningMetadata,
    [uri] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $IsccPath,
    [string] $SignToolPath = 'signtool.exe',
    [switch] $Unsigned
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src/Regedit/Regedit.csproj'

if (-not $IsccPath) {
    $IsccPath = (Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue).Source
    if (-not $IsccPath) {
        $IsccPath = @(
            "$env:ProgramFiles/Inno Setup 7/ISCC.exe"
            "${env:ProgramFiles(x86)}/Inno Setup 7/ISCC.exe"
            "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe"
            "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe"
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Install Inno Setup 6.3 or later, or pass -IsccPath.'
}

if ($Unsigned) {
    if ($CertificateThumbprint -or $MachineCertificateStore -or $TrustedSigningDll -or $TrustedSigningMetadata) {
        throw '-Unsigned cannot be combined with signing credentials.'
    }
}
else {
    $SignToolPath = (Get-Command $SignToolPath -CommandType Application -ErrorAction Stop).Source
    if (-not $TimestampUrl.IsAbsoluteUri -or $TimestampUrl.Scheme -notin @('http', 'https')) {
        throw 'Pass an absolute HTTP or HTTPS RFC 3161 timestamp URL.'
    }
    $signArguments = @('/fd', 'sha256', '/td', 'sha256', '/tr', $TimestampUrl.AbsoluteUri)
    if ($CertificateThumbprint -and -not ($TrustedSigningDll -or $TrustedSigningMetadata)) {
        $signArguments += @('/sha1', $CertificateThumbprint, '/s', 'My')
        if ($MachineCertificateStore) { $signArguments += '/sm' }
    }
    elseif ($TrustedSigningDll -and $TrustedSigningMetadata -and -not ($CertificateThumbprint -or $MachineCertificateStore)) {
        $signArguments += @('/dlib', (Resolve-Path -LiteralPath $TrustedSigningDll).Path,
                            '/dmdf', (Resolve-Path -LiteralPath $TrustedSigningMetadata).Path)
    }
    else {
        throw 'Pass -CertificateThumbprint, both Trusted Signing paths, or -Unsigned for local testing.'
    }
    $innoSignCommand = ((@($SignToolPath, 'sign') + $signArguments | ForEach-Object {
        if ($_.Contains('"')) { throw 'Signing arguments cannot contain double quotes.' }
        '$q' + $_.Replace('$', '$$') + '$q'
    }) -join ' ') + ' $f'
}

function Assert-TrustedSignature([string] $Path, [switch] $RequireTimestamp) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -eq $signature.SignerCertificate.Issuer -or
        ($RequireTimestamp -and -not $signature.TimeStamperCertificate)) {
        throw "A valid, CA-issued Authenticode signature is required: $Path"
    }
}

function Sign-File([string] $Path) {
    & $SignToolPath sign @signArguments $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $Path" }
    Assert-TrustedSignature $Path -RequireTimestamp
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$installerRoot = Join-Path $artifactsRoot 'installers'
$outputRoot = Join-Path $installerRoot $(if ($Unsigned) { 'unsigned' } else { 'signed' })
foreach ($directory in @($artifactsRoot, $installerRoot, $outputRoot)) {
    if (Test-Path -LiteralPath $directory) {
        $entry = Get-Item -LiteralPath $directory -Force
        if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Installer output must be a regular directory: $directory"
        }
    }
}
if ([IO.Path]::GetDirectoryName($outputRoot) -ne $installerRoot) {
    throw 'Installer output must stay inside artifacts/installers.'
}

Push-Location $repositoryRoot
try {
    $propertyOutput = dotnet msbuild $project -p:Configuration=Release -getProperty:Version,Company
    if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate installer version and publisher.' }
    $properties = ($propertyOutput -join [Environment]::NewLine | ConvertFrom-Json).Properties
    if ([string]::IsNullOrWhiteSpace($properties.Version) -or [string]::IsNullOrWhiteSpace($properties.Company)) {
        throw 'The project must define Version and Company.'
    }
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
    foreach ($platform in @('x64', 'ARM64')) {
        $arch = $platform.ToLowerInvariant()
        $publishDirectory = Join-Path $outputRoot "win-$arch/publish"
        dotnet publish $project -c Release "-p:Platform=$platform" "-p:PublishProfile=win-$arch" -o $publishDirectory -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw "Publishing win-$arch failed." }
        $executable = Join-Path $publishDirectory 'regedit2.exe'
        if (-not (Test-Path -LiteralPath $executable) -or
            (Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File -Recurse)) {
            throw "Invalid self-contained installer payload for win-$arch."
        }
        Copy-Item -LiteralPath LICENSE.txt, NOTICE.md -Destination $publishDirectory
        if (-not $Unsigned) {
            $binaries = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Where-Object Extension -in '.exe', '.dll', '.mui', '.winmd')
            foreach ($binary in $binaries) {
                $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
                if ($signature.Status -eq 'NotSigned') {
                    Sign-File $binary.FullName
                }
                else {
                    Assert-TrustedSignature $binary.FullName
                }
            }
        }
        $compilerArguments = @('/Q', "/DArch=$arch", "/DVersion=$($properties.Version)",
                               "/DPublisher=$($properties.Company)", "/DSourceDir=$publishDirectory", "/DOutputDir=$outputRoot")
        if ($Unsigned) { $compilerArguments += '/DUnsigned' }
        else {
            New-Item -ItemType Directory -Path (Join-Path (Split-Path -Parent $publishDirectory) 'signed-uninstaller') -Force | Out-Null
            $compilerArguments += "/SRegeditStoreSign=$innoSignCommand"
        }
        & $IsccPath @compilerArguments (Join-Path $PSScriptRoot 'Regedit.iss')
        if ($LASTEXITCODE -ne 0) { throw "Compiling the win-$arch installer failed." }
        $suffix = if ($Unsigned) { '-unsigned' } else { '' }
        $setup = Join-Path $outputRoot "Regedit-$($properties.Version)-win-$arch-Setup$suffix.exe"
        if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Installer output is missing: $setup" }
        if (-not $Unsigned) { Assert-TrustedSignature $setup -RequireTimestamp }
    }
    Write-Host "Installer artifacts: $outputRoot"
}
finally {
    Pop-Location
}
