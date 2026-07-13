# MirageDeck — готовая сборка

Этот пакет собран GitHub Actions для Windows 11 x64.

## Установка

1. Распакуйте ZIP полностью в отдельную папку.
2. Откройте PowerShell от имени администратора в распакованной папке:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\scripts\verify-package.ps1
   .\scripts\install-driver.ps1 -DriverDirectory .\driver
   ```

3. Обычным двойным щелчком запустите:

   ```text
   panel\MiraboxEmulator.exe
   ```

4. Дождитесь статуса `ГОТОВО` и сообщения `HID 5548:1021 (Global) готов к работе`, затем запустите Stream Dock. Если установщик запросил перезагрузку, выполните её перед запуском панели.

Для удаления откройте PowerShell от администратора и выполните:

```powershell
.\scripts\uninstall-driver.ps1
```

Скрипт удаления также удаляет тестовые сертификаты MirageDeck из системных
хранилищ `Root` и `TrustedPublisher`. Передайте
`-KeepTestCertificate`, только если их требуется намеренно сохранить.

## Подпись

Драйвер является UMDF 2 DLL и не содержит собственного kernel-mode `.sys`.
Пакет подписан временным самоподписанным сертификатом, созданным отдельно для
этой CI-сборки. Установочный скрипт импортирует его публичную часть из
`driver\MiraboxHIDEmulator-Test.cer` в системные хранилища `Root` и
`TrustedPublisher`. Поэтому Test Mode включать и Secure Boot отключать не
требуется. Для публичного распространения без изменения системного хранилища
доверия потребуется release-подпись, которой Windows уже доверяет, например
Microsoft attestation/WHQL.
