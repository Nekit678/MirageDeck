[CmdletBinding()]
param([switch]$KeepTestCertificate)

$ErrorActionPreference = "Stop"

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

$devices = Get-PnpDevice -PresentOnly:$false |
  Where-Object InstanceId -Like "ROOT\*" |
  Where-Object {
    $hardwareIds = Get-PnpDeviceProperty `
      -InstanceId $_.InstanceId `
      -KeyName "DEVPKEY_Device_HardwareIds" `
      -ErrorAction SilentlyContinue
    @($hardwareIds.Data) -contains "Root\MiraboxN4Pro"
  }
foreach ($device in $devices) {
  Invoke-Native -FilePath "pnputil.exe" -Arguments @("/remove-device", $device.InstanceId)
}

if (-not $KeepTestCertificate) {
  # package-ci.ps1 creates a new certificate for every CI package with this
  # project-specific subject. Remove current and stale package certificates.
  $testCertificateSubject = "CN=Mirabox HID Emulator Test"
  $certificateStores = @(
    "Cert:\LocalMachine\Root",
    "Cert:\LocalMachine\TrustedPublisher"
  )

  foreach ($store in $certificateStores) {
    $certificates = @(
      Get-ChildItem -Path $store |
        Where-Object Subject -EQ $testCertificateSubject
    )
    foreach ($certificate in $certificates) {
      Write-Host "Removing MirageDeck test certificate $($certificate.Thumbprint) from $store..."
      Remove-Item -LiteralPath $certificate.PSPath -Force
    }
  }
}

Write-Host "Virtual Mirabox device instances were removed."
if ($KeepTestCertificate) {
  Write-Host "MirageDeck test certificates were kept because -KeepTestCertificate was specified."
} else {
  Write-Host "MirageDeck test certificates were removed from Root and TrustedPublisher."
}
Write-Host "The driver package can be removed with pnputil /delete-driver oemNN.inf /uninstall."
