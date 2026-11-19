# AutoCQ 0.9.12

## Русский

Исправлена запись связи с корреспондентом, который ответил с задержкой. Если AutoCQ уже успел вызвать другую станцию, при завершении первой связи JTDX теперь берёт отправленный рапорт и время начала обмена из её недавних передач.

Для каждого позывного запоминаются время первого рапорта и значение последнего. При продолжении прерванной связи в журнал попадают эти данные. Они доступны в течение пяти минут после последнего переданного рапорта. Исправление действует в JTDX и во внешнем журнале, например LogHX.

На стенде проверены поздние ответы после смены корреспондента, повтор рапорта, переход через полночь и обновление файлов JTDX.

**Как обновиться.** Откройте **«Обновления»** в AutoCQ, проверьте выпуск и скачайте его. После окончания связи закройте JTDX и нажмите **«Обновить и перезапустить»**. Вместе с AutoCQ обновится дополнение для JTDX. Если вы пользуетесь LogHX, запустите JTDX его значком после подготовки.

[Сайт UB3DQY](https://ub3dqy.com/) · [Описание AutoCQ](https://ub3dqy.com/ru/read/5/) · [Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Ошибки и предложения](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

## English

Fixed logging when a station replies late. If AutoCQ has already called another station, JTDX now takes the sent report and exchange start time for the resumed contact from recent transmissions to that callsign.

AutoCQ's JTDX integration keeps the time of the first report and the value of the last report for the current attempt. It retains those details for five minutes. The correction applies to both the JTDX log and external logbooks such as LogHX.

Offline tests cover late replies after changing stations, repeated reports, UTC midnight, and the JTDX patch upgrade.

**Updating.** Open **«Обновления»** (Updates) in AutoCQ, check for the release, and download it. Finish the contact, close JTDX, and click **«Обновить и перезапустить»** (Update and restart). This updates AutoCQ and its JTDX integration. If you use LogHX, launch JTDX from its icon after preparation.

[UB3DQY website](https://ub3dqy.com/) · [About AutoCQ](https://ub3dqy.com/ru/read/5/) · [User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [Bugs and ideas](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)
