# AutoCQ 0.9.51

## Русский

- После первого сетевого сбоя AutoCQ повторяет запрос к ленте спотов через минуту. При повторных сбоях можно воспользоваться кнопкой **«Обновить споты»**, когда пройдёт минута после последней попытки. AutoCQ соблюдает интервалы, заданные сервером.
- Поиск POTA и WWFF подхватывает свежие цифровые споты из DX-кластеров, если в комментарии указан номер активации. **«Кластеры LogHX»** работают и при выборе только POTA или WWFF. Если файл спотов занят записью, AutoCQ проверит его снова и сохранит свежие споты из предыдущих пакетов.
- В окне **«Экспедиции и редкие станции»** отдельно видны недоступные источники и те, в которых пока нет свежих спотов FT8 и FT4. Наведите курсор на строку над таблицей, чтобы прочитать её целиком.
- Откройте **«Обновления»**. Завершите текущую связь и закройте JTDX перед командой **«Обновить и перезапустить»**. Если используете LogHX, снова откройте JTDX его значком в журнале.

[Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Сайт UB3DQY](https://ub3dqy.com/ru/read/5/)

## English

- AutoCQ retries a live spot feed one minute after the first network error. If errors continue, use **«Обновить споты»** (Refresh spots) one minute after the last attempt. AutoCQ respects the server's requested wait.
- POTA and WWFF searches use recent digital DX cluster spots whose comments identify an activation. **«Кластеры LogHX»** (LogHX clusters) also works when POTA or WWFF is your only target. If LogHX holds the file open while writing, AutoCQ checks again and keeps recent spots from earlier batches.
- **Expeditions and rare stations** shows which sources are unavailable and which have no recent FT8 or FT4 spots. Hover over the status above the table to see the full line.
- Open **«Обновления»** (Updates). Finish the current contact and close JTDX before clicking **«Обновить и перезапустить»** (Update and restart). If you use LogHX, launch JTDX from its icon afterward.

[User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [UB3DQY website](https://ub3dqy.com/ru/read/5/)
