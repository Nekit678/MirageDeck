<p align="center">
  <img src="docs/assets/miragedeck-logo.png" width="220" alt="Логотип MirageDeck">
</p>

<h1 align="center">MirageDeck</h1>

<p align="center">
  Виртуальный Mirabox Stream Dock N4 Pro для Windows 11.<br>
  Полноценная HID-эмуляция, экранная панель и управление без физического устройства.
</p>

<p align="center">
  <a href="README.md">Русский</a> · <a href="README.en.md">English</a>
</p>

<p align="center">
  <a href="https://github.com/Nekit678/MiraboxHIDEmulator/actions/workflows/build-windows.yml"><img alt="Сборка" src="https://github.com/Nekit678/MiraboxHIDEmulator/actions/workflows/build-windows.yml/badge.svg"></a>
  <img alt="Windows 11 x64" src="https://img.shields.io/badge/Windows_11-x64-0078D4?logo=windows11&logoColor=white">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="Статус версии" src="https://img.shields.io/badge/version-1.x%20early%20release-ffb500">
</p>

> [!WARNING]
> **MirageDeck 1.x — первая публичная мажорная версия проекта.** Основные сценарии уже работают, но возможны ошибки, несовместимость с отдельными версиями Stream Dock и ещё не изученные команды протокола. Проект будет развиваться: ожидаются исправления, улучшения совместимости и новые возможности. Перед установкой прочитайте раздел [«Ограничения»](#ограничения).

MirageDeck создаёт в Windows виртуальное HID-устройство с профилем глобального **Mirabox N4ProE `5548:1021`**. Официальное приложение Stream Dock распознаёт его как обычную аппаратную панель, отправляет изображения клавиш и touch bar, а отдельное WinForms-приложение показывает их на экране и возвращает нажатия, вращения энкодеров и жесты.

## Демонстрация

<p align="center">
  <img src="docs/assets/panel-preview.png" width="900" alt="Интерфейс виртуальной панели MirageDeck">
</p>

<p align="center"><sub>Статический предпросмотр текущего интерфейса. Изображения клавиш и touch bar в работающей панели поступают непосредственно из Stream Dock.</sub></p>

```text
Stream Dock ──изображения и команды──▶ виртуальный HID ──▶ панель MirageDeck
Stream Dock ◀──клавиши, энкодеры, жесты── виртуальный HID ◀── панель MirageDeck
```

## Возможности

- виртуальный **UMDF 2 HID minidriver**, совместимый с профилем N4 Pro firmware `02.009`;
- 10 LCD-клавиш, 4 нажимаемых энкодера и сенсорный экран с двумя режимами;
- отображение PNG, JPEG и raw BGR24, переданных приложением Stream Dock;
- нажатия и отпускания клавиш, клики и вращение энкодеров;
- Button Mode: 4 экранные функции энкодеров и горизонтальные свайпы между страницами;
- Touchbar Mode: координатные касания, тапы и горизонтальный drag;
- яркость, очистка, пробуждение экрана и автоматическое переключение режима по командам протокола;
- потоковый декодер команд `BAT`, `LOG`, `BGPIC`, `MOD`, `LIG`, `CLE`, `DIS` и `STP`;
- кроссплатформенные тесты ядра и готовый `win-x64` пакет из GitHub Actions.

## Быстрый старт

> [!IMPORTANT]
> CI-пакет подписан временным самоподписанным сертификатом. Установочный скрипт добавляет его публичную часть в системные хранилища `Root` и `TrustedPublisher`, поэтому потребуются права администратора. MirageDeck использует UMDF 2 и не содержит собственного kernel-mode `.sys`: **Test Mode включать и Secure Boot отключать не требуется**. Устанавливайте только пакет из доверенного запуска CI.

### Готовый пакет из GitHub Actions

1. Откройте **[Actions → Build Windows package](https://github.com/Nekit678/MiraboxHIDEmulator/actions/workflows/build-windows.yml)**.
2. Выберите последний успешный запуск и скачайте артефакт `MiraboxHIDEmulator-win-x64`.
3. Полностью распакуйте ZIP в отдельную папку.
4. Откройте PowerShell от имени администратора в папке пакета:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\scripts\verify-package.ps1
   .\scripts\install-driver.ps1 -DriverDirectory .\driver
   ```

5. Запустите `panel\MiraboxEmulator.exe`, дождитесь статуса **«ГОТОВО»**, затем откройте Stream Dock. Если установщик запросил перезагрузку, выполните её перед запуском панели.

Подробная инструкция также находится в `START-HERE.md` внутри артефакта. Артефакты GitHub Actions хранятся 30 дней; ZIP с исходным кодом из меню **Code** не является готовой сборкой.

### Сборка из исходников

Понадобятся:

- Windows 11 22H2 или новее;
- Visual Studio 2022 с компонентом **Desktop development with C++**;
- Windows 11 SDK и Windows Driver Kit (WDK);
- .NET 8 SDK;
- права администратора для установки драйвера.

В **Developer PowerShell for VS 2022**:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1 Debug
dotnet run --project .\test\Mirabox.Emulator.Core.Tests -c Release
.\scripts\install-driver.ps1 -DriverDirectory .\driver\x64\Debug
dotnet run --project .\src\Mirabox.Emulator.Panel -c Release
```

## Управление

| Элемент панели | Действие мышью | Событие устройства |
| --- | --- | --- |
| LCD-клавиша | нажать и отпустить | key down / key up |
| Энкодер | щелчок | аппаратное нажатие |
| Энкодер | колесо мыши над ручкой | вращение влево / вправо |
| Touch display, Button Mode | щелчок по одному из 4 сегментов | функция энкодера |
| Touch display, Button Mode | горизонтальный свайп | предыдущая / следующая страница |
| Touch display, Touchbar Mode | тап | активация элемента по координате |
| Touch display, Touchbar Mode | горизонтальный drag | последовательность `ARX`-координат |

Режим touch display выбирает Stream Dock. У физического N4 Pro вертикальный свайп обрабатывается самой прошивкой и не имеет отдельного HID-кода, поэтому MirageDeck не может переключать режим приложения таким жестом.

## Как это устроено

```mermaid
flowchart LR
    SD[Mirabox Stream Dock] <-->|HID reports| DRV[UMDF 2 virtual HID]
    DRV <-->|feature channel| PANEL[WinForms panel]
    PANEL --> CORE[Protocol decoder]
    CORE --> UI[Keys · touch display · encoders]
    UI -->|input reports| DRV
```

| Каталог | Назначение |
| --- | --- |
| [`driver/`](driver/) | виртуальный UMDF 2 HID-драйвер |
| [`src/Mirabox.Emulator.Core/`](src/Mirabox.Emulator.Core/) | профиль устройства, input reports и декодер протокола |
| [`src/Mirabox.Emulator.Panel/`](src/Mirabox.Emulator.Panel/) | Windows-панель и служебный HID-канал |
| [`test/`](test/) | автономные тесты формирования и декодирования пакетов |
| [`scripts/`](scripts/) | сборка, упаковка, проверка, установка и удаление |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | исследованная часть протокола N4 Pro |

## Ограничения

- Поддерживается только глобальный **N4ProE `VID_5548&PID_1021`**. Китайский вариант с PID `1008` и другие модели Mirabox имеют отличающиеся профили.
- Совместимость может зависеть от версии Stream Dock. Неизвестные команды безопасно возвращаются как `UnknownCommandUpdate`, но их эффект ещё не реализован.
- Драйвер эмулирует HID-функцию, но не USB topology и USB descriptors физического composite-устройства. Программы, проверяющие USB parent, могут не обнаружить MirageDeck.
- Публичный пакет пока использует локально доверенный самоподписанный сертификат. Test Mode для текущего UMDF-драйвера не нужен, однако корпоративные политики WDAC/App Control могут запретить такой пакет. Для распространения без добавления сертификата в системное хранилище потребуется release-подпись, которой Windows уже доверяет, например Microsoft attestation/WHQL.
- Проект тестировался на Windows 11 x64; другие версии и архитектуры пока не заявлены как поддерживаемые.

## Удаление

В PowerShell от имени администратора:

```powershell
.\scripts\uninstall-driver.ps1
```

Скрипт удаляет виртуальное устройство и все сертификаты с проектным subject `CN=Mirabox HID Emulator Test` из `Root` и `TrustedPublisher`. Чтобы намеренно сохранить сертификаты:

```powershell
.\scripts\uninstall-driver.ps1 -KeepTestCertificate
```

## Планы

- исправлять найденные ошибки и регрессии Stream Dock;
- расширять покрытие неизвестных команд протокола;
- улучшать диагностику подключения и журналирование;
- упростить выпуск подписанных и версионированных сборок;
- расширять автоматические тесты драйвера и интерфейса.

Нашли ошибку? Создайте [issue](https://github.com/Nekit678/MiraboxHIDEmulator/issues) и приложите версию Windows, версию Stream Dock, шаги воспроизведения и, если возможно, HID-трассировку. Инструкция по исследованию неизвестных пакетов находится в [`docs/PROTOCOL.md`](docs/PROTOCOL.md).

## Благодарности и правовая информация

Профиль устройства основан на открытом [StreamDock Device SDK](https://github.com/MiraboxSpace/StreamDock-Device-SDK) и проверенной реализации протокола семейства 293. Драйверная часть основана на примере Microsoft `vhidmini2`; файлы каталога [`driver/`](driver/) распространяются на условиях MS-PL, приведённых в [`driver/LICENSE-MS-PL`](driver/LICENSE-MS-PL).

MirageDeck — независимый проект и не связан с Mirabox, HOTSPOTEK или Microsoft. Названия и товарные знаки принадлежат их владельцам.
