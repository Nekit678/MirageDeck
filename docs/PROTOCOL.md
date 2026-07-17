# Реализованный HID-профиль Stream Deck +

Источник форматов — официальная документация Elgato
[Stream Deck HID API](https://docs.elgato.com/streamdeck/hid/stream-deck-plus/)
и её [general reference](https://docs.elgato.com/streamdeck/hid/general/).
Все многобайтовые числа little-endian.

## Identity и descriptor

| Поле | Значение |
| --- | --- |
| Модель | Stream Deck + / `20GBD9901` |
| VID / PID | `0FD9:0084` |
| Manufacturer / Product | `Elgato` / `Stream Deck +` |
| Usage page / usage | vendor-defined `FF00:0001` |
| Input report | ID `01`, 512 байт вместе с ID |
| Output report | ID `02`, 1024 байта вместе с ID |
| Feature reports | IDs `03..08`, `0A`, по 32 байта вместе с ID |

В descriptor report count не включает report ID: `511`, `1023` и `31` байт
соответственно. Панель не добавляет приватные HID reports: служебный обмен идёт
через отдельный device interface `{2F5E3A9C-54A4-4A18-A73D-61F40EB0D92B}` и
два private IOCTL. Поэтому `HidP_GetCaps` видит аппаратные размеры Stream Deck +.

## Input reports

Общий заголовок:

```text
offset  size  значение
00      1     report ID = 01
01      1     command
02      2     payload length
04      ...   payload
```

### Клавиши — command `00`

Payload length `08`; bytes `04..0B` содержат состояния клавиш `0..7` в
экранном порядке слева направо, сверху вниз. `01` — pressed, `00` — released.
Каждое событие содержит полный массив состояний, поэтому одновременные нажатия
не теряются.

### Touch strip — command `02`

Координаты находятся в логическом пространстве `800×100`.

| Contents type (`byte 04`) | Payload length | Поля |
| --- | ---: | --- |
| `01` TAP | `0A` | X `06..07`, Y `08..09` |
| `02` PRESS | `0A` | X `06..07`, Y `08..09` |
| `03` FLICK | `0E` | start X/Y `06..09`, end X/Y `0A..0D` |

Событие отправляется после завершения жеста. Панель считает неподвижный контакт
длительностью от 500 мс `PRESS`, более короткий — `TAP`, а перемещение не менее
10 экранных пикселей — `FLICK`.

### Энкодеры — command `03`

Payload length `05`; `byte 04` выбирает contents type:

- `00` BTN: bytes `05..08` — полный массив состояний четырёх кнопок энкодеров;
- `01` ROTATE: bytes `05..08` — знаковые `INT8` ticks, положительные по часовой
  стрелке, отрицательные против.

## Output reports

Остаток каждого report дополнен нулями до 1024 байт. JPEG может занимать
несколько последовательно нумерованных chunks.

| Command | Назначение | Заголовок / data offset |
| --- | --- | --- |
| `07` | изображение клавиши `120×120` | button `02`, done `03`, size `04`, chunk `06`, data `08` |
| `08` | полный LCD `800×480` | done `03`, size `04`, chunk `06`, data `08` |
| `0B` | весь touch strip `800×100` | done `03`, size `04`, chunk `06`, data `08` |
| `0C` | область touch strip | X/Y/W/H `02..09`, done `0A`, chunk `0B`, size `0D`, data `10` |

`done=01` завершает transfer. Decoder отклоняет пропущенный/повторный chunk,
несовпадающую цель, завышенный contents size, button index вне `0..7` и
partial rectangle вне `800×100`.

## Feature reports

Setter reports имеют ID `03`:

| Command | Действие | Параметры |
| --- | --- | --- |
| `02` | показать logo / standby | — |
| `05` | залить LCD | RGB в `02..04` |
| `06` | залить клавишу | index `02`, RGB `03..05` |
| `08` | яркость | `00..64` в `02` |
| `0D` | sleep duration | signed seconds в `02..05` |

Getter reports:

| ID | Ответ эмулятора |
| --- | --- |
| `04`, `05`, `07` | LD / AP2 / AP1 firmware string в `06..0D` |
| `06` | 12-байтовый serial string |
| `08` | 2×4 keys, key `120×120`, LCD `800×480`, 24 bpp |
| `0A` | сохранённый sleep duration |

## Служебный канал панели

Драйвер перехватывает output и setter feature reports в кольцевую очередь на
128 элементов. `IOCTL_MIRAGE_GET_CAPTURE` возвращает один report либо kind `0`,
если очередь пуста. `IOCTL_MIRAGE_INJECT_INPUT` принимает ровно 512 байт с ID
`01` и завершает ожидающий HID read; если read ещё не выставлен, input report
сохраняется в отдельной кольцевой очереди.

Такой канал не использует feature report с увеличенным размером: приложение
Elgato продолжает работать с документированным максимумом 32 байта.
