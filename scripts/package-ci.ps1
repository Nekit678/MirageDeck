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

function Find-WdkTool {
  param([Parameter(Mandatory)][string]$Name)
  $roots = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
    "${env:ProgramFiles(x86)}\Windows Kits\11\bin",
    (Join-Path (Get-Location) "packages")
  ) | Where-Object { Test-Path $_ }
  $matches = foreach ($root in $roots) {
    Get-ChildItem $root -Filter $Name -File -Recurse -ErrorAction SilentlyContinue
  }
  $preferred = $matches | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
  if (-not $preferred) { $preferred = $matches | Sort-Object FullName -Descending | Select-Object -First 1 }
  if (-not $preferred) { throw "$Name was not found in the installed WDK" }
  return $preferred.FullName
}

if (-not $IsWindows) { throw "Driver packaging must run on Windows" }
if (-not (Test-Path $PanelPublishDirectory)) { throw "Panel publish directory was not found: $PanelPublishDirectory" }

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
Copy-Item $driverDll.FullName, $driverInf.FullName -Destination $driverOutput
Copy-Item (Join-Path $PanelPublishDirectory "*") -Destination $panelOutput -Recurse
Copy-Item "scripts/install-driver.ps1", "scripts/uninstall-driver.ps1", "scripts/verify-package.ps1" -Destination $scriptsOutput
Copy-Item "DISTRIBUTION.md" -Destination (Join-Path $OutputDirectory "START-HERE.md")

$certificate = New-SelfSignedCertificate `
  -Type Custom `
  -Subject "CN=Mirabox HID Emulator Test" `
  -FriendlyName "Mirabox HID Emulator CI Test Certificate" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -KeyAlgorithm RSA `
  -KeyLength 2048 `
  -KeyExportPolicy Exportable `
  -HashAlgorithm SHA256 `
  -NotAfter (Get-Date).AddYears(2) `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

$certificatePath = Join-Path $driverOutput "MiraboxHIDEmulator-Test.cer"
Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT | Out-Null
Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\CurrentUser\TrustedPublisher" | Out-Null

$signTool = Find-WdkTool "signtool.exe"
$inf2Cat = Find-WdkTool "inf2cat.exe"
$packagedDll = Join-Path $driverOutput "MiraboxN4Pro.dll"
$catalog = Join-Path $driverOutput "MiraboxN4Pro.cat"

# The catalog hashes the driver binary, so embed-sign the DLL first, create the
# catalog second, and sign the completed catalog last.
Invoke-Native $signTool sign /v /fd SHA256 /sha1 $certificate.Thumbprint $packagedDll
Invoke-Native $inf2Cat "/driver:$driverOutput" /os:10_X64 /uselocaltime
if (-not (Test-Path $catalog)) { throw "Inf2Cat did not create MiraboxN4Pro.cat" }
Invoke-Native $signTool sign /v /fd SHA256 /sha1 $certificate.Thumbprint $catalog
Invoke-Native $signTool verify /pa /v $packagedDll
Invoke-Native $signTool verify /pa /v $catalog

$buildInfo = @(
  "Build commit: $($env:BUILD_COMMIT ?? 'local')"
  "Build run: $($env:BUILD_RUN ?? 'local')"
  "Built at UTC: $([DateTime]::UtcNow.ToString('O'))"
  "Certificate subject: $($certificate.Subject)"
  "Certificate thumbprint: $($certificate.Thumbprint)"
  "Certificate expires: $($certificate.NotAfter.ToUniversalTime().ToString('O'))"
)
Set-Content (Join-Path $OutputDirectory "BUILD-INFO.txt") $buildInfo -Encoding UTF8

$checksumPath = Join-Path $OutputDirectory "SHA256SUMS.txt"
$checksums = Get-ChildItem $OutputDirectory -File -Recurse |
  Where-Object FullName -NE $checksumPath |
  Sort-Object FullName |
  ForEach-Object {
    $relative = [IO.Path]::GetRelativePath((Resolve-Path $OutputDirectory), $_.FullName).Replace('\', '/')
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
  }
Set-Content $checksumPath $checksums -Encoding ASCII

Write-Host "Distribution assembled at $OutputDirectory"
