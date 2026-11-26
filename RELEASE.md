# AutoCQ 0.9.40

## Русский

- Если корреспондент передал рапорт другой станции, JTDX сохраняет назначенный повтор RR73. Если JTDX всё же передал рапорт в слоте повтора, AutoCQ дослушивает ответ и продолжает сеанс с учётом лимита попыток.
- AutoCQ закрывает файл журнала JTDX сразу после чтения. Если файл временно занят, модуль дочитает его в следующем цикле.
- Исправление входит в приложение и дополнение JTDX. Установите выпуск через **«Обновления»**. Перед кнопкой **«Обновить и перезапустить»** закройте JTDX после окончания текущей связи. При работе с LogHX снова откройте JTDX его значком в журнале.

[Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Сайт UB3DQY](https://ub3dqy.com/ru/read/5/)

## English

- A report addressed to another station leaves the scheduled RR73 repeat in place. If JTDX still sends a report in the retry slot, AutoCQ listens for the reply and continues the session within the configured retry limit.
- AutoCQ closes the JTDX log as soon as it has read the new data. If the file is temporarily busy, it retries on the next cycle.
- The release updates AutoCQ and its JTDX integration. Open **«Обновления»** (Updates). Finish the current contact and close JTDX before clicking **«Обновить и перезапустить»** (Update and restart). If you use LogHX, launch JTDX from its icon afterward.

[User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [UB3DQY website](https://ub3dqy.com/ru/read/5/)
