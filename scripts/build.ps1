param([ValidateSet("Debug", "Release")][string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

dotnet build "$root\src\Mirabox.Emulator.Panel\Mirabox.Emulator.Panel.csproj" -c $Configuration

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
  -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuild) { throw "Visual Studio 2022 + WDK не найдены" }
& $msbuild "$root\driver\MiraboxN4Pro.vcxproj" /p:Configuration=$Configuration /p:Platform=x64 /m

