# AutoCQ 0.9.0

## Русский

Модуль для Windows x64 и JTDX 2.2.160-32A-rc10 Win64.

- Позывной, добавленный вручную, AutoCQ вызывает напрямую после текущего QSO и доступных входящих вызовов.
- В режиме мониторинга можно задать страны DXCC, позывные и маски. AutoCQ вызывает только выбранные цели по CQ, 73 или RRR из левого окна, затем возвращается на приём. Галочка «Одна связь на цель на диапазоне» учитывает журнал JTDX.
- Приоритетные DXCC в обычном режиме теперь можно вызвать и по RR73, принятому в левом окне.

В архиве находятся AutoCQ, инструкция и новый патч для JTDX. При обновлении закройте обе программы и запустите `Patch-JTDX.ps1` из архива. Скрипт использует сохранённый оригинал rc10 для обновления предыдущего патча. [Инструкция и все сборки JTDX](https://github.com/ub3dqy/jtdx-autocq#скачать-jtdx-22160-rc10-для-windows) · [Статья об AutoCQ](https://ub3dqy.com/ru/read/5/)

## English

AutoCQ runs on 64-bit Windows with JTDX 2.2.160-32A-rc10 Win64.

- AutoCQ calls manually queued callsigns directly once the current QSO and any waiting incoming calls have been handled.
- In monitoring mode, choose DXCC entities, callsigns, or patterns. AutoCQ calls only those targets after CQ, 73, or RRR appears in the left pane, then returns to receive. The one-contact-per-target-per-band option uses the JTDX log.
- In regular mode, a recent RR73 in the left pane can also trigger a call to a priority DXCC station.

The archive contains AutoCQ, instructions, and an updated JTDX patch. To upgrade, close both programs and run `Patch-JTDX.ps1` from the archive. The script uses the original rc10 backup to update the previous patch. [Instructions and JTDX downloads](https://github.com/ub3dqy/jtdx-autocq/blob/main/README.en.md#download-jtdx-22160-rc10-for-windows) · [About AutoCQ](https://ub3dqy.com/en/read/5/)

[JTDX 32A Win64](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa](https://ecoalfa.org/recursos/jtdx.4/) · [JTDX SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
