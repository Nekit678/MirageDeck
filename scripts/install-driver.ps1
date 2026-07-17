[CmdletBinding()]
param(
  [string]$DriverDirectory = "",
  [string]$TestCertificatePath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Native {
  param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments)
  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $FilePath $Arguments" }
}

function Get-MirageDeckRootDevice {
  Get-PnpDevice -PresentOnly:$false |
    Where-Object InstanceId -Like "ROOT\*" |
    Where-Object {
      $hardwareIds = Get-PnpDeviceProperty `
        -InstanceId $_.InstanceId `
        -KeyName "DEVPKEY_Device_HardwareIds" `
        -ErrorAction SilentlyContinue
      @($hardwareIds.Data) -contains "Root\StreamDeckPlusEmulator"
    }
}

function Get-DeviceClassGuid {
  param([Parameter(Mandatory)]$Device)
  $property = Get-PnpDeviceProperty `
    -InstanceId $Device.InstanceId `
    -KeyName "DEVPKEY_Device_ClassGuid" `
    -ErrorAction SilentlyContinue
  if ($property.Data) { return $property.Data.ToString() }
  return ""
}

function Test-CertificateInStore {
  param(
    [Parameter(Mandatory)][string]$Store,
    [Parameter(Mandatory)][string]$Thumbprint
  )
  Test-Path -LiteralPath "Cert:\LocalMachine\$Store\$Thumbprint"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw "Run PowerShell as Administrator."
}

$packageRoot = Split-Path -Parent $PSScriptRoot
$packageInfoPath = Join-Path $packageRoot "PACKAGE-INFO.json"
$packageInfo = $null
if (Test-Path -LiteralPath $packageInfoPath -PathType Leaf) {
  $verifier = Join-Path $PSScriptRoot "verify-package.ps1"
  if (-not (Test-Path -LiteralPath $verifier -PathType Leaf)) {
    throw "Package verifier was not found: $verifier"
  }
  Write-Host "Verifying the complete development package before changing the system..."
  & $verifier -PackageRoot $packageRoot
  $packageInfo = Get-Content -LiteralPath $packageInfoPath -Raw | ConvertFrom-Json
  if (-not $TestCertificatePath) {
    $TestCertificatePath = Join-Path $packageRoot $packageInfo.certificateFile
  }
}

if ($packageInfo -and $packageInfo.driverKind -eq "kernel-ude") {
  $systemStartOptions = (Get-ItemProperty `
    -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Control" `
    -Name SystemStartOptions `
    -ErrorAction SilentlyContinue).SystemStartOptions
  if ($systemStartOptions -notmatch '(^|\s)TESTSIGNING(\s|$)') {
    throw @"
This development package contains a self-signed kernel-mode UDE driver.
Windows Test Mode is not active, so Windows would refuse to start it.

To enable it, run in an elevated PowerShell:
  bcdedit.exe /set testsigning on
Then restart Windows and run this installer again.

If Windows reports that Secure Boot policy protects this setting, disable
Secure Boot in the firmware first. Production packages require Microsoft
driver signing and do not use Test Mode.
"@
  }
}

if (-not $DriverDirectory) {
  $DriverDirectory = if ($packageInfo) {
    Join-Path $packageRoot "driver"
  } else {
    Join-Path (Split-Path -Parent $PSScriptRoot) "driver\x64\Debug"
  }
}
$inf = Get-ChildItem $DriverDirectory -Filter StreamDeckPlusEmulator.inf -File -Recurse | Select-Object -First 1
if (-not $inf) { throw "StreamDeckPlusEmulator.inf was not found under $DriverDirectory" }
if ($packageInfo) {
  $expectedInf = [IO.Path]::GetFullPath((Join-Path $packageRoot $packageInfo.driverInf))
  if ($inf.FullName -ne $expectedInf) {
    throw "A packaged installation must use its verified INF: $expectedInf"
  }
}

if ($TestCertificatePath) {
  $TestCertificatePath = (Resolve-Path $TestCertificatePath).Path
  $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($TestCertificatePath)
  if ($certificate.HasPrivateKey) {
    throw "Refusing to import a certificate file that contains a private key"
  }
  $certificateSha256 = (Get-FileHash -LiteralPath $TestCertificatePath -Algorithm SHA256).Hash
  if ($packageInfo -and $certificateSha256 -ne $packageInfo.certificateFingerprintSha256) {
    throw "The certificate SHA-256 fingerprint does not match PACKAGE-INFO.json"
  }
  if ($certificate.NotAfter -le [DateTime]::Now) {
    throw "The test certificate expired at $($certificate.NotAfter.ToString('O'))"
  }

  $needsRoot = -not (Test-CertificateInStore "Root" $certificate.Thumbprint)
  $needsPublisher = -not (Test-CertificateInStore "TrustedPublisher" $certificate.Thumbprint)
  if ($needsRoot -or $needsPublisher) {
    $manifestHash = if (Test-Path -LiteralPath (Join-Path $packageRoot "SHA256SUMS.txt")) {
      (Get-FileHash -LiteralPath (Join-Path $packageRoot "SHA256SUMS.txt") -Algorithm SHA256).Hash
    } else {
      "not available (local source build)"
    }

    Write-Host ""
    Write-Host "MirageDeck development package" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "This package will add the following public certificate to:"
    Write-Host "- LocalMachine\Root"
    Write-Host "- LocalMachine\TrustedPublisher"
    Write-Host ""
    Write-Host "Subject: $($certificate.Subject)"
    Write-Host "Issuer: $($certificate.Issuer)"
    Write-Host "SHA-256 fingerprint: $certificateSha256"
    Write-Host "Valid until: $($certificate.NotAfter.ToString('O'))"
    Write-Host "SHA256SUMS.txt hash: $manifestHash"
    Write-Host ""
    $confirmation = Read-Host "Type INSTALL to continue"
    if ($confirmation -cne "INSTALL") {
      throw "Installation was cancelled; the certificate was not imported"
    }

    if ($needsRoot) {
      Invoke-Native -FilePath "certutil.exe" -Arguments @("-f", "-addstore", "Root", $TestCertificatePath)
    } else {
      Write-Host "Certificate is already present in LocalMachine\Root."
    }
    if ($needsPublisher) {
      Invoke-Native -FilePath "certutil.exe" -Arguments @("-f", "-addstore", "TrustedPublisher", $TestCertificatePath)
    } else {
      Write-Host "Certificate is already present in LocalMachine\TrustedPublisher."
    }
  } else {
    Write-Host "The exact package certificate is already trusted in Root and TrustedPublisher."
  }
} elseif ($packageInfo) {
  throw "The development package certificate is missing"
} else {
  Write-Warning "No test certificate was selected. The driver must already have a trusted signature."
  Write-Warning "For a local test build, pass its public .cer explicitly with -TestCertificatePath."
}

$existingDevices = @(Get-MirageDeckRootDevice)
Invoke-Native -FilePath "pnputil.exe" -Arguments @("/add-driver", $inf.FullName, "/install")

$helperPath = Join-Path $PSScriptRoot "RootDeviceInstaller.cs"
if (-not (Test-Path $helperPath)) { throw "SetupAPI helper was not found: $helperPath" }
if (-not ([System.Management.Automation.PSTypeName]'Mirabox.Emulator.Install.RootDeviceInstaller').Type) {
  Add-Type -Path $helperPath
}

if ($existingDevices.Count -gt 0 -and @(
    $existingDevices | Where-Object {
      (Get-DeviceClassGuid $_) -ne "{36fc9e60-c465-11cf-8056-444553540000}"
    }
  ).Count -gt 0) {
  Write-Host "Replacing the legacy HID-class root device with a USB-class controller..."
  foreach ($device in $existingDevices) {
    Invoke-Native -FilePath "pnputil.exe" -Arguments @("/remove-device", $device.InstanceId)
  }
  $rebootRequired = [Mirabox.Emulator.Install.RootDeviceInstaller]::Install(
    $inf.FullName,
    "Root\StreamDeckPlusEmulator"
  )
} elseif ($existingDevices.Count -gt 0) {
  Write-Host "Updating driver for $($existingDevices.Count) existing MirageDeck virtual device(s)..."
  $rebootRequired = [Mirabox.Emulator.Install.RootDeviceInstaller]::Update(
    $inf.FullName,
    "Root\StreamDeckPlusEmulator"
  )
  foreach ($device in $existingDevices) {
    Invoke-Native -FilePath "pnputil.exe" -Arguments @("/restart-device", $device.InstanceId)
  }
} else {
  Write-Host "Creating the persistent MirageDeck virtual USB controller..."
  $rebootRequired = [Mirabox.Emulator.Install.RootDeviceInstaller]::Install(
    $inf.FullName,
    "Root\StreamDeckPlusEmulator"
  )
}
Invoke-Native -FilePath "pnputil.exe" -Arguments @("/scan-devices")

if ($rebootRequired) {
  Write-Host "The MirageDeck virtual USB device was installed. Restart Windows before launching the panel."
} else {
  $usbDevice = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object InstanceId -Like "USB\VID_0FD9&PID_0084*" |
    Select-Object -First 1
  if (-not $usbDevice -or $usbDevice.Status -ne "OK") {
    throw "The driver package was installed, but USB\\VID_0FD9&PID_0084 did not start correctly. Restart Windows and check Device Manager."
  }
  Write-Host "The MirageDeck virtual USB device was installed as $($usbDevice.InstanceId)."
  Write-Host "Launch the panel, then the Elgato Stream Deck app."
}
