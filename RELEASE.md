# AutoCQ 0.9.16

## Русский

- При ручном вызове и после завершения автоматической попытки AutoCQ продолжает мониторинг, когда JTDX подтверждает выбранную станцию или возврат на приём. Запоздавший статус прежнего позывного отменяется новым.
- Если 73, RRR или RR73 приходит до обмена рапортами, AutoCQ продолжает применять лимит вызовов. По исчерпании попыток модуль возвращается на приём или переходит к следующей станции.
- В журнале различаются исчерпанный лимит, отсутствие декодирования и досрочное завершение вызова в JTDX.

**Как обновиться.** Откройте **«Обновления»**, скачайте выпуск и закройте JTDX перед установкой. Затем нажмите **«Обновить и перезапустить»**. При работе с LogHX запускайте JTDX значком в журнале.

[Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Сайт UB3DQY](https://ub3dqy.com/ru/read/5/)

## English

- Monitoring continues when JTDX confirms a manual selection or a return to receive after an automatic attempt. The fresh status supersedes any delayed status for the previous callsign.
- The configured call limit still applies if 73, RRR or RR73 arrives before reports are exchanged. When the limit is reached, AutoCQ returns to receive or moves on to the next station.
- The log distinguishes an exhausted attempt limit, a station that could not be decoded and a call that JTDX ended early.

**Updating.** Open **«Обновления»** (Updates), download the release, and close JTDX before installation. Click **«Обновить и перезапустить»** (Update and restart). If you use LogHX, launch JTDX from its icon.

[User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [UB3DQY website](https://ub3dqy.com/ru/read/5/)
