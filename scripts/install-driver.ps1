param([string]$DriverDirectory = "")
$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw "Запустите PowerShell от имени администратора"
}
if (-not $DriverDirectory) {
  $DriverDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) "driver\x64\Debug"
}
$inf = Get-ChildItem $DriverDirectory -Filter MiraboxN4Pro.inf -Recurse | Select-Object -First 1
if (-not $inf) { throw "MiraboxN4Pro.inf не найден в $DriverDirectory" }
if (Get-PnpDevice -PresentOnly | Where-Object InstanceId -Like "ROOT\MIRABOXN4PRO*") {
  Write-Host "Виртуальный Mirabox уже установлен."
  exit 0
}

$certificate = Get-ChildItem $DriverDirectory -Filter *.cer -Recurse | Select-Object -First 1
if ($certificate) {
  & certutil.exe -f -addstore Root $certificate.FullName
  & certutil.exe -f -addstore TrustedPublisher $certificate.FullName
}
& pnputil.exe /add-driver $inf.FullName /install
$devgen = Get-Command devgen.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $devgen) {
  $kitsTools = "${env:ProgramFiles(x86)}\Windows Kits\10\Tools"
  $devgen = Get-ChildItem $kitsTools -Filter devgen.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object FullName -Match '\\x64\\devgen\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
}
if (-not $devgen) {
  throw "devgen.exe отсутствует. Требуется Windows 11 22H2+; либо выполните: devcon install `"$($inf.FullName)`" Root\MiraboxN4Pro"
}
& $devgen /add /bus ROOT /hardwareid Root\MiraboxN4Pro
& pnputil.exe /scan-devices
Write-Host "Виртуальный Mirabox установлен. Запустите панель, затем Stream Dock."
