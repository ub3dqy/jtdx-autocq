# AutoCQ 0.9.8

## Русский

Мониторинг теперь отвечает станциям, которые сами вас зовут, даже если их нет среди выбранных стран, позывных или масок. Фильтр определяет, кого модуль ищет и вызывает сам. Входящие вызовы проверяются первыми.

Исправлен случай, когда финальное 73 от текущего корреспондента и вызов другой станции приходят в одном приёмном периоде. AutoCQ завершает QSO, отвечает следующему вызывающему и после обработки очереди возвращается к мониторингу. Это работает и без новой записи в журнале JTDX.

Если выбранная станция ещё не ответила, а другая уже зовёт вас, модуль переключится на неё после приёмного периода. Идущая передача и связь с отвечающим корреспондентом не прерываются. При отсутствии ответа действует заданный лимит попыток.

Цели и оставшееся время сохраняются. «Одна связь на цель» и пятиминутная пауза поиска не блокируют входящий вызов. При нескольких вызывающих сначала выбирается новая станция на этом диапазоне и в этом виде работы, затем учитываются приоритеты DXCC и порядок очереди. Исключения DXCC и настройка JTDX «Отв. B4» остаются в силе.

Старый позывной, оставшийся в поле DX при выключенной передаче, больше не мешает принять новый входящий вызов этой станции.

По окончании времени завершается текущая связь и доступная очередь входящих вызовов, принятых до истечения таймера. Новые вызовы после этого не продлевают сеанс. Обновлены оба README, руководства, офлайн-помощь и демонстрационные снимки; добавлен пример ответа вне фильтра мониторинга.

**Как обновиться с 0.9.6 или 0.9.7:** откройте **«Обновления»**, проверьте выпуск и скачайте его. Завершите текущую связь, закройте JTDX и нажмите **«Обновить и перезапустить»**. LogHX можно оставить открытым. Патч JTDX из 0.9.3–0.9.7 подходит для этой версии.

[Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Статья на сайте](https://ub3dqy.com/ru/read/5/) · [Ошибки и предложения](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

## English

Monitoring now answers stations calling you, even when they are outside the selected DXCC entities, callsigns or patterns. The target filter determines whom AutoCQ searches for and calls itself. Incoming callers are checked first.

Fixed the case where the current station's final 73 and another station's call arrive in the same receive period. AutoCQ finishes the QSO, answers the next caller and resumes monitoring after the queue is handled. This also works without a new JTDX log entry.

If the selected station has not answered and someone else calls you, AutoCQ switches after the receive period. It does not interrupt a transmission or a QSO with a responding station. The configured attempt limit still applies when a station does not answer.

Targets and time remaining are preserved. The one-contact-per-target limit and five-minute search pause do not block an incoming caller. New stations on the current band and mode come first; DXCC priorities and queue order then apply. DXCC exclusions and JTDX's Answer B4 setting remain in force.

A callsign left in the DX field with transmission disabled no longer blocks a fresh incoming call from that station.

At the deadline, AutoCQ finishes the current QSO and handles available callers received before time expired. New calls after expiry do not extend the session. Updated both READMEs, user guides, offline help and genuine demo screenshots, with a new example of answering a caller outside the monitoring filter.

**Updating from 0.9.6 or 0.9.7:** open **«Обновления»** (Updates), check and download the release. Finish the contact, close JTDX and click **«Обновить и перезапустить»**. LogHX can remain open. The JTDX patch from 0.9.3–0.9.7 is compatible.

[User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [Project article](https://ub3dqy.com/ru/read/5/) · [Bugs and ideas](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

[JTDX 32A Win64 for AutoCQ](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa release](https://ecoalfa.org/recursos/jtdx.4/) · [Installer SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
