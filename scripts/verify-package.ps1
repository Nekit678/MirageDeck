[CmdletBinding()]
param([string]$PackageRoot = "")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $PackageRoot) {
  $PackageRoot = Split-Path -Parent $PSScriptRoot
}
$root = (Resolve-Path $PackageRoot).Path
$checksumFile = Join-Path $root "SHA256SUMS.txt"
$packageInfoFile = Join-Path $root "PACKAGE-INFO.json"

function Get-RelativePackagePath {
  param([Parameter(Mandatory)][string]$Path)
  $rootUri = [Uri]::new($root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar)
  $fileUri = [Uri]::new([IO.Path]::GetFullPath($Path))
  return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($fileUri).ToString()).Replace('\', '/')
}

function Get-PackagePath {
  param([Parameter(Mandatory)][string]$RelativePath)

  $normalized = $RelativePath.Replace('\', '/')
  if ([IO.Path]::IsPathRooted($normalized) -or $normalized -match '(^|/)\.\.(/|$)') {
    throw "Unsafe package path: $RelativePath"
  }
  $candidate = [IO.Path]::GetFullPath((Join-Path $root $normalized))
  $prefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
  if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package path escapes the package root: $RelativePath"
  }
  return $candidate
}

function Get-CertificateSha256 {
  param([Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

  $sha256 = [Security.Cryptography.SHA256]::Create()
  try {
    return ([BitConverter]::ToString($sha256.ComputeHash($Certificate.RawData))).Replace('-', '')
  } finally {
    $sha256.Dispose()
  }
}

function Assert-AuthenticodeSigner {
  param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$ExpectedCertificateSha256
  )

  $signature = Get-AuthenticodeSignature -FilePath $Path
  if (-not $signature.SignerCertificate) {
    throw "No Authenticode signature was found on $Path"
  }
  $signatureStatus = $signature.Status.ToString()
  if ($signatureStatus -in @('HashMismatch', 'NotSigned', 'NotSupported')) {
    throw "Invalid Authenticode signature on ${Path}: $($signature.StatusMessage)"
  }
  $actualSignerSha256 = Get-CertificateSha256 $signature.SignerCertificate
  if ($actualSignerSha256 -ne $ExpectedCertificateSha256) {
    throw "Unexpected Authenticode signer on $Path"
  }
  Write-Host "[verify] Signature matches the package certificate: $([IO.Path]::GetFileName($Path))"
}

if (-not (Test-Path -LiteralPath $checksumFile -PathType Leaf)) {
  throw "SHA256SUMS.txt not found"
}
if (-not (Test-Path -LiteralPath $packageInfoFile -PathType Leaf)) {
  throw "PACKAGE-INFO.json not found"
}

$packageInfo = Get-Content -LiteralPath $packageInfoFile -Raw | ConvertFrom-Json
$requiredProperties = @(
  'schemaVersion', 'product', 'packageKind', 'version', 'driverKind', 'driverInf',
  'driverBinary', 'driverCatalog', 'panelExecutable', 'certificateFile',
  'certificateThumbprintSha1', 'certificateFingerprintSha256'
)
foreach ($property in $requiredProperties) {
  if (-not $packageInfo.PSObject.Properties[$property] -or -not $packageInfo.$property) {
    throw "PACKAGE-INFO.json is missing '$property'"
  }
}
if ($packageInfo.schemaVersion -ne 2 -or $packageInfo.product -ne 'MirageDeck') {
  throw "Unsupported package metadata"
}
if ($packageInfo.packageKind -ne 'development') {
  throw "This verifier only accepts MirageDeck development packages"
}
if ($packageInfo.driverKind -ne 'kernel-ude') {
  throw "This package does not contain the expected UDE kernel driver"
}
$expectedLayout = @{
  driverInf = 'driver/StreamDeckPlusEmulator.inf'
  driverBinary = 'driver/StreamDeckPlusEmulator.sys'
  driverCatalog = 'driver/StreamDeckPlusEmulator.cat'
  panelExecutable = 'panel/MirageDeck.exe'
  certificateFile = 'driver/MirageDeck-CI-Test.cer'
}
foreach ($property in $expectedLayout.Keys) {
  if (($packageInfo.$property).Replace('\', '/') -ne $expectedLayout[$property]) {
    throw "Unexpected package layout for ${property}: $($packageInfo.$property)"
  }
}

$manifestEntries = @{}
foreach ($line in Get-Content -LiteralPath $checksumFile) {
  if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
    throw "Invalid checksum line: $line"
  }
  $expected = $Matches[1]
  $relative = $Matches[2].Replace('\', '/')
  if ($manifestEntries.ContainsKey($relative)) {
    throw "Duplicate checksum entry: $relative"
  }
  $path = Get-PackagePath $relative
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Package file is missing: $relative"
  }
  $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) {
    throw "Checksum mismatch: $relative"
  }
  $manifestEntries[$relative] = $true
}

$unlistedFiles = @(
  Get-ChildItem -LiteralPath $root -File -Recurse |
    Where-Object FullName -NE $checksumFile |
    ForEach-Object {
      Get-RelativePackagePath $_.FullName
    } |
    Where-Object { -not $manifestEntries.ContainsKey($_) }
)
if ($unlistedFiles.Count -gt 0) {
  throw "Files not covered by SHA256SUMS.txt: $($unlistedFiles -join ', ')"
}
Write-Host "[verify] All package files are covered by valid SHA-256 checksums."

$requiredFiles = @(
  'BUILD-INFO.txt', 'START-HERE.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
  'scripts/install-driver.ps1', 'scripts/uninstall-driver.ps1',
  'scripts/verify-package.ps1', 'scripts/RootDeviceInstaller.cs',
  $packageInfo.driverInf, $packageInfo.driverBinary, $packageInfo.driverCatalog,
  $packageInfo.panelExecutable, $packageInfo.certificateFile
)
foreach ($relative in $requiredFiles) {
  if (-not (Test-Path -LiteralPath (Get-PackagePath $relative) -PathType Leaf)) {
    throw "Required package file is missing: $relative"
  }
}

$forbiddenExtensions = @('.pfx', '.p12', '.p8', '.key', '.pem', '.snk')
$forbiddenNames = @('.env', 'id_rsa', 'id_ed25519', 'secrets.json', 'credentials.json')
$forbiddenFiles = @(
  Get-ChildItem -LiteralPath $root -File -Recurse |
    Where-Object {
      $_.Extension.ToLowerInvariant() -in $forbiddenExtensions -or
      $_.Name.ToLowerInvariant() -in $forbiddenNames
    }
)
if ($forbiddenFiles.Count -gt 0) {
  throw "Private keys or secret-like files were found: $($forbiddenFiles.Name -join ', ')"
}

$allowedBinaries = @(
  $packageInfo.driverBinary.Replace('\', '/'),
  $packageInfo.driverCatalog.Replace('\', '/'),
  $packageInfo.panelExecutable.Replace('\', '/')
)
$unexpectedBinaries = @(
  Get-ChildItem -LiteralPath $root -File -Recurse |
    Where-Object { $_.Extension.ToLowerInvariant() -in @('.exe', '.dll', '.sys', '.cat', '.msi', '.com', '.scr') } |
    ForEach-Object { Get-RelativePackagePath $_.FullName } |
    Where-Object { $_ -notin $allowedBinaries }
)
if ($unexpectedBinaries.Count -gt 0) {
  throw "Unexpected executable files were found: $($unexpectedBinaries -join ', ')"
}
Write-Host "[verify] No private keys, secret-like files, or unexpected executables were found."

$certificatePath = Get-PackagePath $packageInfo.certificateFile
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($certificate.HasPrivateKey) {
  throw "The packaged certificate unexpectedly contains a private key"
}
if ($certificate.Thumbprint -ne $packageInfo.certificateThumbprintSha1) {
  throw "The certificate SHA-1 thumbprint does not match PACKAGE-INFO.json"
}
$certificateSha256 = Get-CertificateSha256 $certificate
if ($certificateSha256 -ne $packageInfo.certificateFingerprintSha256) {
  throw "The certificate SHA-256 fingerprint does not match PACKAGE-INFO.json"
}
if ($certificate.NotAfter -le [DateTime]::Now) {
  throw "The development certificate expired at $($certificate.NotAfter.ToString('O'))"
}
Write-Host "[verify] Certificate SHA-256: $certificateSha256"

$infPath = Get-PackagePath $packageInfo.driverInf
$inf = Get-Content -LiteralPath $infPath -Raw
$escapedVersion = [Regex]::Escape([string]$packageInfo.version)
if ($inf -notmatch "(?im)^DriverVer\s*=\s*\d{2}/\d{2}/\d{4},$escapedVersion\s*$") {
  throw "DriverVer does not match package version $($packageInfo.version)"
}
if ($inf -notmatch '(?im)^Provider="MirageDeck Project"\s*$' -or
    $inf -notmatch '(?im)^Manufacturer="MirageDeck Project"\s*$') {
  throw "The INF provider/manufacturer identity is not MirageDeck Project"
}
if ($inf -notmatch '(?im)^CatalogFile=StreamDeckPlusEmulator\.cat\s*$' -or
    $inf -notmatch '(?im)^ServiceBinary="%13%\\StreamDeckPlusEmulator\.sys"\s*$' -or
    $inf -notmatch '(?im)^Class=USB\s*$' -or
    $inf -notmatch '(?im)^%DeviceDesc%=StreamDeckPlusEmulator,Root\\StreamDeckPlusEmulator\s*$') {
  throw "The INF package names or compatibility hardware ID are unexpected"
}

$panelPath = Get-PackagePath $packageInfo.panelExecutable
$panelFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($panelPath).FileVersion
if ($panelFileVersion -ne $packageInfo.version) {
  throw "Panel file version '$panelFileVersion' does not match package version '$($packageInfo.version)'"
}

Assert-AuthenticodeSigner (Get-PackagePath $packageInfo.driverBinary) $certificateSha256
Assert-AuthenticodeSigner (Get-PackagePath $packageInfo.driverCatalog) $certificateSha256
Assert-AuthenticodeSigner $panelPath $certificateSha256

Write-Host "Package verification succeeded: MirageDeck $($packageInfo.version) development artifact."
