$ErrorActionPreference = "Stop"
$devices = Get-PnpDevice -PresentOnly:$false |
  Where-Object InstanceId -Like "ROOT\*" |
  Where-Object {
    $hardwareIds = Get-PnpDeviceProperty `
      -InstanceId $_.InstanceId `
      -KeyName "DEVPKEY_Device_HardwareIds" `
      -ErrorAction SilentlyContinue
    @($hardwareIds.Data) -contains "Root\MiraboxN4Pro"
  }
foreach ($device in $devices) { & pnputil.exe /remove-device $device.InstanceId }
Write-Host "Virtual Mirabox device instances were removed. The driver package can be removed with pnputil /delete-driver oemNN.inf /uninstall."
