# AutoCQ 0.9.3

## Русский

Модуль для Windows x64 и JTDX 2.2.160-32A-rc10 Win64.

Исправлена автозапись повторных связей в FT8 и FT4. JTDX записывает отдельное QSO с позывным, который ещё числится среди двух последних записанных, и передаёт запись в подключённый журнал, например LogHX. Повтор финального сообщения в рамках одной связи не дублирует запись.

Для обновления закройте JTDX и AutoCQ, примените `Patch-JTDX.ps1` из архива 0.9.3 и запустите `JTDX-AutoCQ.exe` из того же архива. [Инструкция и все сборки JTDX](https://github.com/ub3dqy/jtdx-autocq#скачать-jtdx-22160-rc10-для-windows) · [Статья об AutoCQ](https://ub3dqy.com/ru/read/5/)

## English

AutoCQ runs on 64-bit Windows with JTDX 2.2.160-32A-rc10 Win64.

Fixed automatic logging of repeat contacts in FT8 and FT4. JTDX logs a separate QSO with a call that is still among its last two logged calls and sends the entry to the connected logbook, such as LogHX. Resending the final message in the same exchange still produces one entry.

To upgrade, close JTDX and AutoCQ, apply `Patch-JTDX.ps1` from the 0.9.3 archive, and open `JTDX-AutoCQ.exe` from the same archive. [Instructions and JTDX downloads](https://github.com/ub3dqy/jtdx-autocq/blob/main/README.en.md#download-jtdx-22160-rc10-for-windows) · [About AutoCQ](https://ub3dqy.com/en/read/5/)

[JTDX 32A Win64](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa](https://ecoalfa.org/recursos/jtdx.4/) · [JTDX SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
