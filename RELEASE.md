# AutoCQ 0.9.2

## Русский

Модуль для Windows x64 и JTDX 2.2.160-32A-rc10 Win64.

- AutoCQ удаляет старый вызов из очереди после завершения QSO, даже если JTDX не создал новую запись. Это включает обмен, в котором наш `73` завершает связь после `RRR` или `RR73`.
- Если JTDX останавливает вызов с причиной `Called station not decoded`, AutoCQ после завершения декодирования выбирает следующую станцию или возвращается к CQ. В режиме мониторинга он возвращается на приём.

Если уже установлен патч из AutoCQ 0.9.0, для обновления достаточно заменить `JTDX-AutoCQ.exe` файлом из архива при закрытом AutoCQ. При первой установке используйте инструкцию. [Инструкция и все сборки JTDX](https://github.com/ub3dqy/jtdx-autocq#скачать-jtdx-22160-rc10-для-windows) · [Статья об AutoCQ](https://ub3dqy.com/ru/read/5/)

## English

AutoCQ runs on 64-bit Windows with JTDX 2.2.160-32A-rc10 Win64.

- AutoCQ removes the old call from the queue after a completed QSO even when JTDX does not write a new log entry. This includes exchanges that end with our `73` in response to an `RRR` or `RR73`.
- After a JTDX `Called station not decoded` stop, AutoCQ waits for decoding to finish and selects the next station or resumes CQ. In monitoring mode it returns to reception.

If you already use the patch from AutoCQ 0.9.0, close AutoCQ and replace `JTDX-AutoCQ.exe` with the file in the archive. For a fresh installation, follow the setup guide. [Instructions and JTDX downloads](https://github.com/ub3dqy/jtdx-autocq/blob/main/README.en.md#download-jtdx-22160-rc10-for-windows) · [About AutoCQ](https://ub3dqy.com/en/read/5/)

[JTDX 32A Win64](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa](https://ecoalfa.org/recursos/jtdx.4/) · [JTDX SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
