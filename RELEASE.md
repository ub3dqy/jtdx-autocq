# AutoCQ 0.9.10

## Русский

Исправлено самопроизвольное выключение мониторинга после завершённого QSO. Если на границе периода JTDX успевал записать начало передачи перед выполнением команды остановки от AutoCQ, модуль мог принять ответ на собственную команду за внешнюю остановку. Теперь он сохраняет ожидание подтверждения, возвращается на приём и продолжает мониторинг. Ручная остановка остаётся действующей.

При скачивании обновления теперь видно, как идёт загрузка. Заполняющаяся шкала показывает процент; под ней указаны полученный и полный объём, скорость, примерное оставшееся и прошедшее время.

Подготовка, подключение к серверу, скачивание, проверка архива и подготовка файлов показаны отдельными этапами. 100% означает получение всего файла; установка становится доступна после успешной проверки. При обрыве соединения загрузка не выдаётся за завершённую, и её можно повторить.

Если сервер не сообщает размер файла, AutoCQ показывает полученный объём и прошедшее время без выдуманного процента. При ожидании новых данных сообщение обновляется, а оценка времени учитывает паузу.

Обновлены русское и английское описания, руководства и офлайн-помощь. Добавлен демонстрационный снимок индикатора загрузки.

**Как обновиться:** откройте **«Обновления»**, проверьте выпуск и скачайте его. После окончания QSO закройте JTDX и нажмите **«Обновить и перезапустить»**. Настройки и очередь сохранятся; LogHX можно оставить открытым. Патч JTDX из 0.9.3–0.9.9 подходит для этой версии.

Новый индикатор работает после установки 0.9.10. При скачивании этой версии из 0.9.9 ещё используется прежнее окно обновлений.

[Сайт UB3DQY](https://ub3dqy.com/) · [Описание AutoCQ](https://ub3dqy.com/ru/read/5/) · [Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Ошибки и предложения](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

## English

Fixed monitoring switching itself off after a completed QSO. When JTDX logged the start of a transmission at the period boundary just before executing AutoCQ's Halt command, the module could mistake its own acknowledgement for an external stop. It now keeps that acknowledgement pending, returns to receive and continues monitoring. Manual stopping still takes effect.

Downloads now show their progress. The filling bar displays the percentage; below it are the received and total size, speed, estimated time remaining and elapsed time.

Preparation, connection, downloading, archive verification and file preparation appear as separate stages. 100% means the file has arrived; installation becomes available only after validation. An interrupted connection is not shown as a completed download, and you can retry.

When the server does not provide the total size, AutoCQ shows received bytes and elapsed time without inventing a percentage. Waiting for more data is reported, and the time estimate accounts for the pause.

Updated both READMEs, user guides and offline help. Added a genuine demonstration screenshot of the download indicator.

**Updating:** open **«Обновления»** (Updates), check and download the release. Finish the QSO, close JTDX and click **«Обновить и перезапустить»**. Settings and the queue are preserved; LogHX can remain open. The JTDX patch from 0.9.3–0.9.9 is compatible.

The new indicator is available after installing 0.9.10. Downloading this release from 0.9.9 still uses that installed version's earlier update window.

[UB3DQY website](https://ub3dqy.com/) · [About AutoCQ](https://ub3dqy.com/ru/read/5/) · [User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [Bugs and ideas](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

[JTDX 32A Win64 for AutoCQ](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa release](https://ecoalfa.org/recursos/jtdx.4/) · [Installer SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
