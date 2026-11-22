# AutoCQ 0.9.17

## Русский

- Если после RR73 корреспондент снова присылает рапорт, AutoCQ повторяет завершающее сообщение ему. Счётчик повторов без ответа каждый раз начинается с нуля.
- Поле **«Повторов 73 без ответа»** задаёт число дополнительных передач после того, как корреспондент перестал отвечать. После каждого повтора модуль прослушивает весь ответный период. Заключительное 73, RR73 или RRR завершает ожидание.

**Как обновиться.** Откройте **«Обновления»**, скачайте выпуск и закройте JTDX перед установкой. Затем нажмите **«Обновить и перезапустить»**. При работе с LogHX запускайте JTDX значком в журнале.

[Руководство](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.md) · [Сайт UB3DQY](https://ub3dqy.com/ru/read/5/)

## English

- If the current station sends another report after RR73, AutoCQ sends the sign-off again and resets the unanswered retry counter.
- **«Повторов 73 без ответа»** (Sign-off retries with no reply) sets the number of extra transmissions once the other station goes quiet. AutoCQ listens through the full reply period after each retry. A final 73, RR73 or RRR ends the wait.

**Updating.** Open **«Обновления»** (Updates), download the release, and close JTDX before installation. Click **«Обновить и перезапустить»** (Update and restart). If you use LogHX, launch JTDX from its icon.

[User guide](https://github.com/ub3dqy/jtdx-autocq/blob/main/USER-GUIDE.en.md) · [UB3DQY website](https://ub3dqy.com/ru/read/5/)
