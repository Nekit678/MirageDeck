# Mirabox HID Emulator — готовая сборка

Этот пакет собран GitHub Actions для Windows 11 x64.

## Установка

1. Распакуйте ZIP полностью в отдельную папку.
2. Включите test signing в PowerShell от администратора и перезагрузитесь:

   ```powershell
   bcdedit /set testsigning on
   shutdown /r /t 0
   ```

3. После перезагрузки откройте PowerShell от администратора в распакованной папке:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\scripts\verify-package.ps1
   .\scripts\install-driver.ps1 -DriverDirectory .\driver
   ```

4. Обычным двойным щелчком запустите:

   ```text
   panel\MiraboxEmulator.exe
   ```

5. Дождитесь статуса `Подключено: HID 5548:1021 (Global)`, затем запустите Stream Dock.

Для удаления откройте PowerShell от администратора и выполните:

```powershell
.\scripts\uninstall-driver.ps1
```

## Подпись

Драйвер подписан временным тестовым сертификатом, созданным отдельно для этой
CI-сборки. Установочный скрипт импортирует публичную часть сертификата из
`driver\MiraboxHIDEmulator-Test.cer`. Для публичного распространения вместо
этого требуется production/attestation signing Microsoft.
