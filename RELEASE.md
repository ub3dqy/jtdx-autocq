# AutoCQ 0.9.39

## Русский

- Исправлена остановка автомата, когда после первого RR73 JTDX снова выбирал рапорт в следующем периоде передачи. При разрешённом повторе AutoCQ сохраняет завершающее сообщение для текущего корреспондента. После ответа или лимита попыток сеанс продолжается.
- Исправление входит в приложение и дополнение JTDX. Установите выпуск через **«Обновления»**. Перед кнопкой **«Обновить и перезапустить»** закройте JTDX после окончания текущей связи. При работе с LogHX снова откройте JTDX его значком в журнале.

[Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Сайт UB3DQY](https://ub3dqy.com/ru/read/5/)

## English

- AutoCQ could stop if JTDX selected a signal report in the slot reserved for an RR73 repeat. AutoCQ now preserves the sign-off for the current station when a repeat is due. The session continues after a reply or after the retry limit.
- The release updates AutoCQ and its JTDX integration. Open **«Обновления»** (Updates). Finish the current contact and close JTDX before clicking **«Обновить и перезапустить»** (Update and restart). If you use LogHX, launch JTDX from its icon afterward.

[User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [UB3DQY website](https://ub3dqy.com/ru/read/5/)
