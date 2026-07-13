[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$DriverSearchRoot,
  [Parameter(Mandatory)][string]$PanelPublishDirectory,
  [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Native {
  param([Parameter(Mandatory)][string]$FilePath, [Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $FilePath $Arguments" }
}

function Assert-SignedBy {
  param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Thumbprint
  )
  $signature = Get-AuthenticodeSignature -FilePath $Path
  if (-not $signature.SignerCertificate) { throw "No Authenticode signature was found on $Path" }
  if ($signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
    throw "Unexpected signing certificate on ${Path}: $($signature.SignerCertificate.Thumbprint)"
  }
  Write-Host "[package] Signature present on $(Split-Path $Path -Leaf); trust is established during target installation"
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

function Find-WdkTool {
  param([Parameter(Mandatory)][string]$Name)
  $patterns = if ($Name -ieq "inf2cat.exe") {
    @(
      "packages\Microsoft.Windows.WDK.x64.*\c\bin\*\x86\Inf2Cat.exe",
      "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x86\Inf2Cat.exe",
      "${env:ProgramFiles(x86)}\Windows Kits\11\bin\*\x86\Inf2Cat.exe"
    )
  } else {
    @(
      "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$Name",
      "${env:ProgramFiles(x86)}\Windows Kits\11\bin\*\x64\$Name"
    )
  }
  $match = Get-Item $patterns -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1
  if (-not $match) { throw "$Name was not found in the installed WDK" }
  return $match.FullName
}

if (-not $IsWindows) { throw "Driver packaging must run on Windows" }
if (-not (Test-Path $PanelPublishDirectory)) { throw "Panel publish directory was not found: $PanelPublishDirectory" }
Write-Host "[package] Validating the SetupAPI root-device helper"
Add-Type -Path "scripts/RootDeviceInstaller.cs"
Write-Host "[package] Locating build outputs"

$driverDll = Get-ChildItem $DriverSearchRoot -Filter MiraboxN4Pro.dll -File -Recurse |
  Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$driverInf = Get-ChildItem $DriverSearchRoot -Filter MiraboxN4Pro.inf -File -Recurse |
  Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $driverDll -or -not $driverInf) { throw "Built MiraboxN4Pro.dll/inf files were not found under $DriverSearchRoot" }

Remove-Item $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
$driverOutput = Join-Path $OutputDirectory "driver"
$panelOutput = Join-Path $OutputDirectory "panel"
$scriptsOutput = Join-Path $OutputDirectory "scripts"
New-Item $driverOutput, $panelOutput, $scriptsOutput -ItemType Directory -Force | Out-Null
Write-Host "[package] Copying panel, driver, scripts, and documentation"
Copy-Item $driverDll.FullName, $driverInf.FullName -Destination $driverOutput
Copy-Item (Join-Path $PanelPublishDirectory "*") -Destination $panelOutput -Recurse
Copy-Item "scripts/install-driver.ps1", "scripts/RootDeviceInstaller.cs", "scripts/uninstall-driver.ps1", "scripts/verify-package.ps1" -Destination $scriptsOutput
Copy-Item "DISTRIBUTION.md" -Destination (Join-Path $OutputDirectory "START-HERE.md")
Copy-Item "LICENSE", "THIRD_PARTY_NOTICES.md" -Destination $OutputDirectory
Copy-Item "driver/LICENSE-MS-PL" -Destination $driverOutput

Write-Host "[package] Creating ephemeral test-signing certificate"
$certificate = $null
try {
  $certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=MirageDeck CI Test" `
    -FriendlyName "MirageDeck ephemeral CI test certificate" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyExportPolicy NonExportable `
    -KeySpec Signature `
    -KeyUsage DigitalSignature `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddDays(30) `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

  Write-Host "[package] Exporting public test certificate"
  $certificatePath = Join-Path $driverOutput "MirageDeck-CI-Test.cer"
  Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT | Out-Null

  Write-Host "[package] Locating SignTool and Inf2Cat"
  $signTool = Find-WdkTool "signtool.exe"
  $inf2Cat = Find-WdkTool "inf2cat.exe"
  Write-Host "[package] SignTool: $signTool"
  Write-Host "[package] Inf2Cat: $inf2Cat"
  $packagedDll = Join-Path $driverOutput "MiraboxN4Pro.dll"
  $packagedInf = Join-Path $driverOutput "MiraboxN4Pro.inf"
  $catalog = Join-Path $driverOutput "MiraboxN4Pro.cat"
  $panelExecutables = @(Get-ChildItem $panelOutput -Filter *.exe -File -Recurse)
  if ($panelExecutables.Count -ne 1) {
    throw "Expected one panel executable, found $($panelExecutables.Count)"
  }
  $panelExecutable = $panelExecutables[0]

  $infText = Get-Content $packagedInf -Raw
  if ($infText -notmatch '(?im)^DriverVer\s*=\s*\d{2}/\d{2}/\d{4},([0-9.]+)\s*$') {
    throw "DriverVer was not found in $packagedInf"
  }
  $packageVersion = $Matches[1]
  $panelFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($panelExecutable.FullName).FileVersion
  if ($panelFileVersion -ne $packageVersion) {
    throw "Panel version $panelFileVersion does not match DriverVer $packageVersion"
  }

  # The catalog hashes the driver binary, so embed-sign the DLL first, create
  # the catalog second, and sign the completed catalog last.
  Write-Host "[package] Signing driver DLL and panel executable"
  Invoke-Native $signTool sign /v /fd SHA256 /s My /sha1 $certificate.Thumbprint $packagedDll
  Invoke-Native $signTool sign /v /fd SHA256 /s My /sha1 $certificate.Thumbprint $panelExecutable.FullName
  Write-Host "[package] Creating driver catalog"
  Invoke-Native $inf2Cat "/driver:$driverOutput" /os:10_X64 /uselocaltime
  if (-not (Test-Path $catalog)) { throw "Inf2Cat did not create MiraboxN4Pro.cat" }
  Write-Host "[package] Signing and verifying catalog"
  Invoke-Native $signTool sign /v /fd SHA256 /s My /sha1 $certificate.Thumbprint $catalog
  Assert-SignedBy $packagedDll $certificate.Thumbprint
  Assert-SignedBy $catalog $certificate.Thumbprint
  Assert-SignedBy $panelExecutable.FullName $certificate.Thumbprint

  $certificateSha256 = Get-CertificateSha256 $certificate
  $packageRoot = (Resolve-Path $OutputDirectory).Path
  $panelRelative = [IO.Path]::GetRelativePath($packageRoot, $panelExecutable.FullName).Replace('\', '/')
  $packageInfo = [ordered]@{
    schemaVersion = 1
    product = "MirageDeck"
    packageKind = "development"
    version = $packageVersion
    hardwareId = "Root\MiraboxN4Pro"
    driverInf = "driver/MiraboxN4Pro.inf"
    driverBinary = "driver/MiraboxN4Pro.dll"
    driverCatalog = "driver/MiraboxN4Pro.cat"
    panelExecutable = $panelRelative
    certificateFile = "driver/MirageDeck-CI-Test.cer"
    certificateSubject = $certificate.Subject
    certificateIssuer = $certificate.Issuer
    certificateThumbprintSha1 = $certificate.Thumbprint
    certificateFingerprintSha256 = $certificateSha256
    certificateNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
  }
  $packageInfo | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory "PACKAGE-INFO.json") -Encoding UTF8

  Write-Host "[package] Writing build metadata and SHA-256 checksums"
  $buildCommit = if ($env:BUILD_COMMIT) { $env:BUILD_COMMIT } else { 'local' }
  $buildRun = if ($env:BUILD_RUN) { $env:BUILD_RUN } else { 'local' }
  $buildInfo = @(
    "Package type: development/CI artifact"
    "Version: $packageVersion"
    "Build commit: $buildCommit"
    "Build run: $buildRun"
    "Built at UTC: $([DateTime]::UtcNow.ToString('O'))"
    "Certificate subject: $($certificate.Subject)"
    "Certificate SHA-1 thumbprint: $($certificate.Thumbprint)"
    "Certificate SHA-256 fingerprint: $certificateSha256"
    "Certificate expires: $($certificate.NotAfter.ToUniversalTime().ToString('O'))"
  )
  Set-Content (Join-Path $OutputDirectory "BUILD-INFO.txt") $buildInfo -Encoding UTF8

  $checksumPath = Join-Path $OutputDirectory "SHA256SUMS.txt"
  $checksums = Get-ChildItem $OutputDirectory -File -Recurse |
    Where-Object FullName -NE $checksumPath |
    Sort-Object FullName |
    ForEach-Object {
      $relative = [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/')
      "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    }
  Set-Content $checksumPath $checksums -Encoding ASCII

  Write-Host "[package] Running the packaged verifier"
  & (Join-Path $scriptsOutput "verify-package.ps1") -PackageRoot $OutputDirectory
  Write-Host "Development distribution assembled at $OutputDirectory"
} finally {
  if ($certificate) {
    Write-Host "[package] Removing the ephemeral private key from the runner"
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
  }
}
