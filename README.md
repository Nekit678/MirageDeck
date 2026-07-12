# Mirabox HID Emulator

Виртуальный **Mirabox Stream Dock N4 Pro** для Windows 11. Stream Dock видит
обычное HID-устройство с идентификаторами глобального N4ProE
`VID_5548&PID_1021`, а отдельная
WinForms-панель показывает переданные устройству изображения и отправляет
нажатия, вращения энкодеров и жесты обратно в программу.

## Что реализовано

- UMDF 2 HID minidriver: Input/Output-часть report descriptor точно повторяет
  прошивку N4 Pro 02.009; payload составляет 512/1024 байта, а буферы Windows
  HID API — 513/1025 байт с ведущим нулевым report ID;
- профиль N4 Pro: 10 LCD-кнопок, 4 нажимаемых энкодера и touch bar с четырьмя визуальными слотами;
- служебный канал виртуальной панели: события передаются обычным HID Output
  report, а пакеты Stream Dock читаются через Feature report; фактические длины
  обоих буферов панель получает из `HIDP_CAPS`;
- разбор команд `BAT`, `LOG`, `BGPIC`, `LIG`, `CLE`, `DIS`, `STP` и сборка
  изображений из нескольких HID-пакетов;
- отрисовка PNG/JPEG на виртуальных кнопках и панели;
- формирование аппаратных пакетов `ACK...OK` для клавиш/энкодеров и
  `ACK...ARX` для touch bar;
- тесты сборки пакетов и потокового декодера.

Профиль основан на открытом [StreamDock Device SDK](https://github.com/MiraboxSpace/StreamDock-Device-SDK)
и проверенной реализации протокола для семейства 293. Драйверная обвязка
основана на Microsoft `vhidmini2` и поэтому файлы в каталоге `driver` содержат
лицензию MS-PL.

## Требования

- Windows 11 22H2 или новее (UMDF HID bridge `MsHidUmdf`);
- Visual Studio 2022 с компонентом **Desktop development with C++**;
- Windows 11 SDK и Windows Driver Kit (WDK);
- .NET 8 SDK;
- права администратора для установки драйвера.

## Онлайн-сборка без Visual Studio/WDK на своём компьютере

В проекте настроен workflow `.github/workflows/build-windows.yml`. Он запускает
тесты, публикует панель как self-contained `win-x64`, собирает UMDF-драйвер,
создаёт отдельный тестовый сертификат, подписывает DLL и CAT, проверяет подписи
и публикует готовый артефакт `MiraboxHIDEmulator-win-x64`.

1. Создайте пустой публичный или приватный репозиторий на GitHub.
2. Отправьте этот проект в ветку `main`.
3. Откройте **Actions → Build Windows package → Run workflow**.
4. После успешной сборки откройте run и скачайте артефакт
   `MiraboxHIDEmulator-win-x64` внизу страницы.
5. Распакуйте ZIP и следуйте `START-HERE.md` внутри него.

Workflow также запускается при push в `main` и проверяет pull requests. Секреты
или собственный сертификат для тестовой сборки не нужны. Артефакт хранится 30
дней. Он предназначен только для разработки: чтобы устанавливать драйвер без
Test Mode, потребуется production/attestation signing Microsoft.

## Сборка

В Developer PowerShell for VS 2022:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1 Debug
dotnet run --project .\test\Mirabox.Emulator.Core.Tests -c Release
```

Visual Studio/WDK создаёт тестовый сертификат для Debug-пакета. На тестовой
машине до установки один раз включите test signing и перезагрузите Windows:

```powershell
bcdedit /set testsigning on
```

При включённом Secure Boot Windows может не позволить включить test signing.
Для распространения нужен нормально подписанный пакет драйвера; обход проверки
подписи в проект не входит.

## Установка и запуск

PowerShell от имени администратора:

```powershell
.\scripts\install-driver.ps1 -DriverDirectory .\driver\x64\Debug
```

Затем:

```powershell
dotnet run --project .\src\Mirabox.Emulator.Panel -c Release
```

1. Убедитесь, что панель показывает `Подключено: HID 5548:1021 (Global)`.
2. Запустите Mirabox Stream Dock.
3. Назначьте действия кнопкам. Переданные PNG/JPEG появятся на панели.
4. Нажатие виртуальной кнопки генерирует down/up. Щелчок по энкодеру генерирует
   press/release, колесо мыши над ним — вращение. По touch bar можно провести
   мышью влево или вправо.

Если устройство уже было создано, повторный запуск install-скрипта не нужен.
Для удаления:

```powershell
.\scripts\uninstall-driver.ps1
```

## Важные ограничения

- Это профиль глобального **N4ProE 5548:1021**. PID `1008` относится к варианту
  для материкового Китая и вызывает региональное предупреждение Stream Dock.
  Другие модели Mirabox имеют другие VID/PID,
  размеры и таблицы аппаратных кодов; простой заменой PID они не становятся
  совместимыми.
- Точная совместимость зависит от версии Stream Dock. В `docs/PROTOCOL.md`
  описано, как снять неизвестный пакет; декодер намеренно отдаёт неизвестные
  команды как `UnknownCommandUpdate`, а не принимает их за изображение.
- Драйвер эмулирует HID-функцию, но не USB topology/USB descriptors физического
  composite-устройства. Текущий Stream Dock и Device SDK обнаруживают устройство
  по HID VID/PID и usage; программа, которая дополнительно требует USB parent,
  потребует USB device emulation вместо HID minidriver.

## Структура

- `driver/` — виртуальный UMDF 2 HID-драйвер;
- `src/Mirabox.Emulator.Core/` — профиль, входные отчёты и декодер;
- `src/Mirabox.Emulator.Panel/` — Windows-панель и служебный HID feature-канал;
- `test/` — кроссплатформенные тесты без сторонних test framework;
- `scripts/` — сборка, установка и удаление.
