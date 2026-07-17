[CmdletBinding()]
param(
  [switch]$KeepTestCertificate,
  [switch]$RemoveDriverPackage,
  [string]$CertificateThumbprint = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Native {
  param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments)
  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $FilePath $Arguments" }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw "Run PowerShell as Administrator."
}

$failures = @()
$devices = @(
  Get-PnpDevice -PresentOnly:$false |
    Where-Object InstanceId -Like "ROOT\*" |
    Where-Object {
      $hardwareIds = Get-PnpDeviceProperty `
        -InstanceId $_.InstanceId `
        -KeyName "DEVPKEY_Device_HardwareIds" `
        -ErrorAction SilentlyContinue
      @($hardwareIds.Data) -contains "Root\StreamDeckPlusEmulator"
    }
)
if ($devices.Count -eq 0) {
  Write-Host "Device: not found (already removed)."
} else {
  foreach ($device in $devices) {
    try {
      Invoke-Native -FilePath "pnputil.exe" -Arguments @("/remove-device", $device.InstanceId)
      Write-Host "Device: removed $($device.InstanceId)."
    } catch {
      $failures += "device $($device.InstanceId): $($_.Exception.Message)"
    }
  }
}

if ($RemoveDriverPackage) {
  try {
    $driverPackages = @(
      Get-WindowsDriver -Online |
        Where-Object { [IO.Path]::GetFileName($_.OriginalFileName) -ieq "StreamDeckPlusEmulator.inf" }
    )
    if ($driverPackages.Count -eq 0) {
      Write-Host "Driver Store: package not found (already removed)."
    } else {
      foreach ($driverPackage in $driverPackages) {
        try {
          Invoke-Native -FilePath "pnputil.exe" -Arguments @(
            "/delete-driver", $driverPackage.Driver, "/uninstall"
          )
          Write-Host "Driver Store: removed $($driverPackage.Driver)."
        } catch {
          $failures += "driver package $($driverPackage.Driver): $($_.Exception.Message)"
        }
      }
    }
  } catch {
    $failures += "Driver Store query: $($_.Exception.Message)"
  }
} else {
  Write-Host "Driver Store: kept (pass -RemoveDriverPackage to remove it explicitly)."
}

if ($KeepTestCertificate) {
  Write-Host "Certificates: kept because -KeepTestCertificate was specified."
} else {
  if (-not $CertificateThumbprint) {
    $packageInfoPath = Join-Path (Split-Path -Parent $PSScriptRoot) "PACKAGE-INFO.json"
    if (Test-Path -LiteralPath $packageInfoPath -PathType Leaf) {
      $packageInfo = Get-Content -LiteralPath $packageInfoPath -Raw | ConvertFrom-Json
      $CertificateThumbprint = $packageInfo.certificateThumbprintSha1
    }
  }

  if (-not $CertificateThumbprint) {
    Write-Warning "Certificates: no exact thumbprint was provided; no certificate was removed."
  } elseif ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
    $failures += "certificate thumbprint has an invalid format"
  } else {
    $CertificateThumbprint = $CertificateThumbprint.ToUpperInvariant()
    foreach ($store in @("Root", "TrustedPublisher")) {
      $certificatePath = "Cert:\LocalMachine\$store\$CertificateThumbprint"
      if (Test-Path -LiteralPath $certificatePath) {
        try {
          Remove-Item -LiteralPath $certificatePath -Force
          Write-Host "Certificate: removed $CertificateThumbprint from LocalMachine\$store."
        } catch {
          $failures += "certificate $CertificateThumbprint in $store`: $($_.Exception.Message)"
        }
      } else {
        Write-Host "Certificate: $CertificateThumbprint not found in LocalMachine\$store (already removed)."
      }
    }
  }
}

if ($failures.Count -gt 0) {
  throw "Uninstall completed with errors: $($failures -join '; ')"
}
Write-Host "MirageDeck uninstall completed successfully."
