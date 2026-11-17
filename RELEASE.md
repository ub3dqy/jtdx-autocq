# AutoCQ 0.9.4

## Русский

Модуль для Windows x64 и JTDX 2.2.160-32A-rc10 Win64.

Свежие вызовы на ваш позывной теперь имеют преимущество перед старыми записями очереди и поиском по левому окну. Если выбранная станция не ответила, а в этом приёмном периоде вас позвала другая, AutoCQ отвечает ей, не дожидаясь исчерпания всех попыток. Место позывного в очереди этому не мешает.

Если выбранная станция ответила, обмен рапортами уже продолжается или ожидается заключительное 73, модуль завершает текущую связь. Решение принимается после декодирования всего приёмного периода, перед следующей передачей. Среди свежих вызывающих сохраняются приоритеты DXCC и исключения. Режим мониторинга продолжает работать только с выбранными целями.

Повторная выгрузка строк JTDX больше не затирает исходный принятый рапорт, если её сообщение с той же отметкой времени короче оригинального.

Для обновления закройте JTDX и AutoCQ и замените модуль файлами из архива 0.9.4. Патч JTDX из 0.9.3 подходит и для этого выпуска. Для более ранних версий примените Patch-JTDX.ps1 из архива.

[Инструкция и все сборки JTDX](https://github.com/ub3dqy/jtdx-autocq#скачать-jtdx-22160-rc10-для-windows) · [Статья об AutoCQ](https://ub3dqy.com/ru/read/5/)

## English

AutoCQ runs on 64-bit Windows with JTDX 2.2.160-32A-rc10 Win64.

Fresh calls addressed to you now take precedence over older queue entries and searches in the left pane. If the selected station does not answer but another station calls you in that receive period, AutoCQ answers that caller without waiting for every attempt to be used. Its position in the queue does not block the reply.

A reply from the selected station, an ongoing report exchange, or the final-73 wait keeps the current contact active. AutoCQ makes the choice after the full receive period has been decoded, before the next transmission. DXCC priorities and exclusions still apply among fresh callers. Monitoring mode continues to call only the selected targets.

Replaying JTDX's decoded messages no longer erases the original received report when the replay contains a shorter message with the same timestamp.

To upgrade, close JTDX and AutoCQ and replace the module with the files from the 0.9.4 archive. The JTDX patch shipped with 0.9.3 is also suitable for this release. When upgrading from an older version, apply Patch-JTDX.ps1 from the archive.

[Instructions and JTDX downloads](https://github.com/ub3dqy/jtdx-autocq/blob/main/README.en.md#download-jtdx-22160-rc10-for-windows) · [About AutoCQ](https://ub3dqy.com/en/read/5/)

[JTDX 32A Win64](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa](https://ecoalfa.org/recursos/jtdx.4/) · [JTDX SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
