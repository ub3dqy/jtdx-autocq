# AutoCQ 0.9.5

## Русский

Модуль для Windows x64 и JTDX 2.2.160-32A-rc10 Win64.

Если в одном приёмном периоде вас позвали несколько станций, AutoCQ сначала отвечает той, с которой ещё не было QSO на текущем диапазоне в текущем режиме. Повторный вызывающий может получить ответ после новых, даже если его позывной стоит выше в очереди или его DXCC выбран приоритетным.

История связей загружается из журнала JTDX при включении автомата. В неё добавляются связи, завершённые в этом сеансе, в том числе подтверждённые финальным сообщением, когда запись в журнале не появилась. Для FT4 также распознаются записи ADIF с MODE=MFSK и SUBMODE=FT4.

Среди новых вызывающих сохраняются приоритеты DXCC и порядок очереди. Исключения DXCC продолжают действовать. Обмен с ответившим корреспондентом и ожидание заключительного сообщения завершаются до выбора следующей станции. Режим мониторинга сохраняет свои условия «Одна связь на цель на диапазоне».

Для обновления закройте JTDX и AutoCQ и замените модуль файлами из архива 0.9.5. Патч JTDX из 0.9.3 или 0.9.4 подходит и для этого выпуска. Для более ранних версий примените Patch-JTDX.ps1 из архива.

[Инструкция и все сборки JTDX](https://github.com/ub3dqy/jtdx-autocq#скачать-jtdx-22160-rc10-для-windows) · [Статья об AutoCQ](https://ub3dqy.com/ru/read/5/)

## English

AutoCQ runs on 64-bit Windows with JTDX 2.2.160-32A-rc10 Win64.

When several stations call you in the same receive period, AutoCQ first answers a station you have not yet worked on the current band in the current mode. Repeat callers can be answered after new callers, even if they appear higher in the queue or their DXCC entity has priority.

AutoCQ loads contact history from the JTDX log when automation starts. Contacts completed during the session are added as well, including exchanges confirmed by a final message when no log entry appeared. FT4 history also recognizes ADIF records with MODE=MFSK and SUBMODE=FT4.

DXCC priorities and queue order still apply among new callers. DXCC exclusions continue to apply. A contact with a station that has answered, including the final-message wait, finishes before another station is selected. Monitoring retains its existing “One contact per target per band” rules.

To upgrade, close JTDX and AutoCQ and replace the module with the files from the 0.9.5 archive. The JTDX patch shipped with 0.9.3 or 0.9.4 is also suitable for this release. When upgrading from an older version, apply Patch-JTDX.ps1 from the archive.

[Instructions and JTDX downloads](https://github.com/ub3dqy/jtdx-autocq/blob/main/README.en.md#download-jtdx-22160-rc10-for-windows) · [About AutoCQ](https://ub3dqy.com/en/read/5/)

[JTDX 32A Win64](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa](https://ecoalfa.org/recursos/jtdx.4/) · [JTDX SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
