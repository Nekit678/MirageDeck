param([string]$DriverDirectory = "")
$ErrorActionPreference = "Stop"

function Invoke-Native {
  param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments)
  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $FilePath $Arguments" }
}

function Get-MiraboxRootDevice {
  Get-PnpDevice -PresentOnly:$false |
    Where-Object InstanceId -Like "ROOT\*" |
    Where-Object {
      $hardwareIds = Get-PnpDeviceProperty `
        -InstanceId $_.InstanceId `
        -KeyName "DEVPKEY_Device_HardwareIds" `
        -ErrorAction SilentlyContinue
      @($hardwareIds.Data) -contains "Root\MiraboxN4Pro"
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw "Run PowerShell as Administrator."
}
if (-not $DriverDirectory) {
  $DriverDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) "driver\x64\Debug"
}
$inf = Get-ChildItem $DriverDirectory -Filter MiraboxN4Pro.inf -Recurse | Select-Object -First 1
if (-not $inf) { throw "MiraboxN4Pro.inf was not found under $DriverDirectory" }
$existingDevices = @(Get-MiraboxRootDevice)

$certificate = Get-ChildItem $DriverDirectory -Filter *.cer -Recurse | Select-Object -First 1
if ($certificate) {
  Invoke-Native -FilePath "certutil.exe" -Arguments @("-f", "-addstore", "Root", $certificate.FullName)
  Invoke-Native -FilePath "certutil.exe" -Arguments @("-f", "-addstore", "TrustedPublisher", $certificate.FullName)
}

Invoke-Native -FilePath "pnputil.exe" -Arguments @("/add-driver", $inf.FullName, "/install")

$helperPath = Join-Path $PSScriptRoot "RootDeviceInstaller.cs"
if (-not (Test-Path $helperPath)) { throw "SetupAPI helper was not found: $helperPath" }
if (-not ([System.Management.Automation.PSTypeName]'Mirabox.Emulator.Install.RootDeviceInstaller').Type) {
  Add-Type -Path $helperPath
}

if ($existingDevices.Count -gt 0) {
  Write-Host "Updating driver for $($existingDevices.Count) existing virtual Mirabox device(s)..."
  $rebootRequired = [Mirabox.Emulator.Install.RootDeviceInstaller]::Update(
    $inf.FullName,
    "Root\MiraboxN4Pro"
  )
  foreach ($device in $existingDevices) {
    Invoke-Native -FilePath "pnputil.exe" -Arguments @("/restart-device", $device.InstanceId)
  }
} else {
  Write-Host "Creating the persistent ROOT\MiraboxN4Pro device..."
  $rebootRequired = [Mirabox.Emulator.Install.RootDeviceInstaller]::Install(
    $inf.FullName,
    "Root\MiraboxN4Pro"
  )
}
Invoke-Native -FilePath "pnputil.exe" -Arguments @("/scan-devices")

if ($rebootRequired) {
  Write-Host "The virtual Mirabox device was installed. Restart Windows before launching the panel."
} else {
  Write-Host "The virtual Mirabox device was installed. Launch the panel, then Stream Dock."
}
