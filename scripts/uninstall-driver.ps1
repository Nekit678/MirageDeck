$ErrorActionPreference = "Stop"
$devices = Get-PnpDevice -PresentOnly:$false | Where-Object InstanceId -Like "ROOT\MIRABOXN4PRO*"
foreach ($device in $devices) { & pnputil.exe /remove-device $device.InstanceId }
Write-Host "Экземпляры виртуального Mirabox удалены. Пакет драйвера можно удалить через pnputil /delete-driver oemNN.inf /uninstall."

