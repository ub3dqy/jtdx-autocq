# AutoCQ 0.9.11

## Русский

Мониторинг продолжается, если JTDX прервал вызов из-за занятой частоты или корреспондент ответил другой станции. AutoCQ возвращается на приём, сохраняет время окончания сеанса и может ответить тем, кто уже вас зовёт. Позывной, который не ответил, появляется во вкладке «Пауза мониторинга».

Исправлена запись отправленного рапорта в FT8 и FT4. При автоматической записи JTDX сверяет рапорт с последним отправленным сообщением. Если оно содержит рапорт текущему корреспонденту, в журнал JTDX и в LogHX уходит значение из передачи. Оно сохраняется и при изменении уровня сигнала на следующем приёме.

Оба исправления проверены на стенде. Обновлены русское и английское описания, руководства и офлайн-помощь.

**Как обновиться.** Откройте **«Обновления»**, проверьте выпуск и скачайте его. После окончания связи закройте JTDX и нажмите **«Обновить и перезапустить»**. Вместе с AutoCQ обновится дополнение JTDX. LogHX можно оставить открытым, а после подготовки снова запустить JTDX из LogHX.

[Сайт UB3DQY](https://ub3dqy.com/) · [Описание AutoCQ](https://ub3dqy.com/ru/read/5/) · [Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Ошибки и предложения](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

## English

Monitoring continues when JTDX ends a call because the frequency is occupied or the other station replies to someone else. AutoCQ returns to receive, keeps the same session end time, and can answer stations already calling you. The unanswered callsign appears in the monitoring retry list.

Fixed the sent report in FT8 and FT4 logs. While logging a contact automatically, JTDX checks the report against the last transmitted message. If that message contains a report for the current contact, the transmitted value goes to both the JTDX log and LogHX. A change in signal strength on the next receive period leaves that value intact.

Both fixes passed offline tests. Updated the Russian and English READMEs, user guides, and offline help.

**Updating.** Open **«Обновления»** (Updates), check for the release, and download it. Finish the contact, close JTDX, and click **«Обновить и перезапустить»** (Update and restart). This updates both AutoCQ and its JTDX integration. LogHX can stay open. Launch JTDX from LogHX after preparation.

[UB3DQY website](https://ub3dqy.com/) · [About AutoCQ](https://ub3dqy.com/ru/read/5/) · [User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [Bugs and ideas](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)
