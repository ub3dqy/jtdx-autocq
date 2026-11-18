# AutoCQ 0.9.6

## Русский

AutoCQ продолжает работу JTDX после завершения связи: отвечает следующему корреспонденту или возвращается к CQ. Для Windows x64 и JTDX 2.2.160-32A-rc10 Win64.

В этом выпуске:

- **Мониторинг по RR73.** Выбранные страны DXCC, позывные и маски теперь вызываются по свежим CQ, 73, RRR и RR73. По прощальному сообщению выбирается его отправитель. После QSO или лимита попыток модуль возвращается на приём.
- **Раздел «Обновления».** Можно проверить новый выпуск, скачать его и установить с перезапуском AutoCQ. Архив, версия файлов и контрольные суммы проверяются перед заменой. Настройки и очередь сохраняются.
- **Автоматические уведомления.** Проверка при запуске и раз в сутки; её можно отключить. Установка начинается только по кнопке и требует закрытого JTDX.
- **Простая первая настройка.** Кнопка «Подготовить» открывает выбор jtdx.exe и сама устанавливает нужное дополнение. Обычному пользователю больше не нужны Node.js и команды PowerShell.
- **Новые инструкции.** Обновлены русское и английское описания, добавлены подробные руководства, офлайн-помощь по кнопке «Как пользоваться» и свежие демонстрационные снимки.
- **Обратная связь на GitHub.** Открыты Issues и Discussions, добавлены формы ошибок и предложений на русском и английском.
- **Паузы мониторинга.** Новая вкладка показывает неответившие станции и отсчёт до следующей попытки. При остановке или выключении мониторинга список и его паузы очищаются.
- **Выбор по 73 от длинных позывных.** Исправлено сопоставление видимой строки с исходным декодированием, когда rc10 при повторной выдаче строк обрезает финальное 73, RRR или RR73. Выбор по-прежнему требует появления станции в левом окне.

В мониторинге сохраняются исключения DXCC, пятиминутная пауза после неудачного вызова и настройка «Одна связь на цель на диапазоне». Если цель — страна и она уже есть в журнале на этом диапазоне, галочку нужно снять для вызова других станций той же страны.

**Переход с 0.9.5 и старше:** закройте JTDX и AutoCQ, распакуйте новый архив в папку модуля с заменой файлов, откройте новый JTDX-AutoCQ.exe и нажмите «Подготовить». Патч JTDX из 0.9.3–0.9.5 подходит и для 0.9.6. Следующие обновления можно устанавливать из окна программы. JTDX при работе с LogHX по-прежнему запускайте через LogHX.

[Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Статья на сайте](https://ub3dqy.com/ru/read/5/) · [Ошибки и предложения](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

## English

AutoCQ keeps JTDX working after a contact, answering the next caller or returning to CQ. For 64-bit Windows and JTDX 2.2.160-32A-rc10 Win64.

This release adds:

- **RR73 monitoring.** DXCC, callsign and pattern targets can now be selected from recent CQ, 73, RRR and RR73 messages. A sign-off selects its sender. The module returns to receive after the QSO or attempt limit.
- **An Updates section.** Check, download and install a release with an AutoCQ restart. Archive checksums and file versions are verified before replacement; settings and queue entries are preserved.
- **Automatic notifications.** Check at startup and daily while open, with an opt-out checkbox. Installation requires your click and JTDX to be closed.
- **Simpler first setup.** Prepare opens a JTDX file selector and installs the integration itself. Ordinary installation no longer needs Node.js or PowerShell commands.
- **New documentation.** Updated Russian and English READMEs, detailed user guides, offline help and current demo screenshots.
- **GitHub feedback.** Issues and Discussions are enabled, with English and Russian bug and feature forms.
- **Monitoring retry pauses.** A new tab lists unanswered stations and time remaining before a retry. Stopping or disabling monitoring clears these pauses.
- **Sign-offs from longer callsigns.** Match a visible row to its original decode when rc10 replay truncates the final 73, RRR or RR73. Selection still requires the station to appear in the left pane.

Monitoring still applies DXCC exclusions, a five-minute retry pause after an unanswered call and “One contact per target per band”. For a DXCC target already present in the log on that band, clear the checkbox to call additional stations from the same entity.

**Upgrading from 0.9.5 or earlier:** close JTDX and AutoCQ, extract the new archive over the module folder and open the new JTDX-AutoCQ.exe. Click Prepare. The 0.9.3–0.9.5 JTDX patch is suitable for this version. Later updates can be installed inside AutoCQ. Continue starting JTDX through LogHX when using that logger.

[User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [Project article](https://ub3dqy.com/ru/read/5/) · [Bugs and ideas](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

[JTDX 32A Win64 for AutoCQ](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa release](https://ecoalfa.org/recursos/jtdx.4/) · [Installer SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
