# AutoCQ 0.9.7

## Русский

В мониторинге теперь можно вручную вызвать интересную станцию, даже если её нет среди выбранных целей. Дважды нажмите на её сообщение в JTDX или выберите строку в AutoCQ и нажмите **«Позвать выбранную»**.

После QSO или лимита вызовов модуль возвращается на приём и продолжает мониторинг. Список целей, настройки и оставшееся время сохраняются. Ручной выбор действует на одну связь; исключения DXCC остаются в силе. Кнопка в AutoCQ позволяет дождаться окончания уже идущего QSO.

Исправлена остановка при ручном выборе: JTDX успевал показать жёлтую строку передачи, а AutoCQ сразу посылал Halt TX. Теперь состояние JTDX и подтверждение ручного выбора из журнала обрабатываются согласованно, в том числе при смене периода передачи.

Также:
- Подтверждённая связь выполняет цель мониторинга даже без новой записи в журнале JTDX.
- Объединена проверка запущенного JTDX; объекты процессов освобождаются после проверки.
- Стендовые проверки и проверка архива удаляют свои временные файлы после завершения.
- Обновлены русское и английское описания, руководства, офлайн-помощь и демонстрационные снимки.

**Как обновиться с 0.9.6:** откройте **«Обновления»**, проверьте выпуск и скачайте его. Завершите текущую связь, закройте JTDX и нажмите **«Обновить и перезапустить»**. LogHX можно оставить открытым. Патч JTDX из 0.9.3–0.9.6 подходит для этой версии.

[Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Статья на сайте](https://ub3dqy.com/ru/read/5/) · [Ошибки и предложения](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

## English

You can now call a station manually while monitoring, even when it is outside your configured targets. Double-click its message in JTDX, or select its row in AutoCQ and click **«Позвать выбранную»** (Call selected).

After the QSO or attempt limit, AutoCQ returns to receive and resumes monitoring. Targets, settings and the original time remaining are preserved. This is a one-contact exception; DXCC exclusions still apply. The AutoCQ button lets the current contact finish before calling your selection.

Fixed the immediate stop after a manual choice: JTDX could show a yellow transmit line while AutoCQ immediately sent Halt TX. Status updates and the logged manual selection are now handled together, including a change of TX period.

Also:
- A confirmed contact completes its monitoring target even when no new JTDX log record appears.
- JTDX process checks share one implementation and dispose their process objects.
- Tests and package verification remove their temporary files when finished.
- Updated Russian and English descriptions, user guides, offline help and genuine demo screenshots.

**Updating from 0.9.6:** open **«Обновления»** (Updates), check and download the release. Finish the contact, close JTDX and click **«Обновить и перезапустить»**. LogHX can remain open. The JTDX patch from 0.9.3–0.9.6 is compatible.

[User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [Project article](https://ub3dqy.com/ru/read/5/) · [Bugs and ideas](https://github.com/ub3dqy/jtdx-autocq/issues/new/choose)

[JTDX 32A Win64 for AutoCQ](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) · [EcoAlfa release](https://ecoalfa.org/recursos/jtdx.4/) · [Installer SHA-256](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)
