# AutoCQ for JTDX

[Русский](README.md)

AutoCQ keeps JTDX on the air after a contact. JTDX can call CQ again or answer a station that is already calling you. You choose how long the session runs. AutoCQ works with FT8 and FT4.

[Download AutoCQ for Windows](https://github.com/ub3dqy/jtdx-autocq/releases/latest) · [AutoCQ article on UB3DQY](https://ub3dqy.com/en/read/5/) · [Russian article](https://ub3dqy.com/ru/read/5/)

Set the timer for an hour, for example, and when it expires AutoCQ will finish the contact in progress, answer any available callers in the queue, then stop. The AutoCQ window shows the remaining time and the stations waiting for a reply.

![AutoCQ in demo mode with a sample caller queue. The current app interface is in Russian.](docs/autocq-demo.png)

## What you can adjust

- **When to look for a station.** Choose how many unanswered CQs to send before AutoCQ looks through received CQs. You can also set how long to keep calling CQ before switching to the other TX period.
- **How long to wait for a reply.** Set the number of call attempts and how many receive periods to wait for a final 73, RR73, or RRR. AutoCQ can then try another station or return to CQ.
- **Which stations to call first.** Choose the DXCC entities you want to prioritize. Before sending its own CQ, AutoCQ can call a priority station if JTDX's left pane shows a recent CQ, 73, RRR, or RR73 from that station. Use the DXCC exclusion list to skip selected entities.
- **How to find a station for an award.** In monitoring mode, AutoCQ listens and calls only your chosen DXCC entities, callsigns, or callsign patterns.
- **How to manage callers.** AutoCQ queues stations that call you while you are working someone else, including messages containing just two callsigns. You can add, move, or remove callsigns and choose how long to keep them in the queue.

Stations calling you now take precedence over older queue entries and searches in the left pane. If the selected station does not reply but another station calls you in that receive period, AutoCQ answers the new caller. A reply from the selected station, or a report exchange already in progress, keeps the current contact active. Your DXCC priorities still apply among callers received in the same period. Monitoring mode continues to call only your selected targets.

When several stations call you in the same period, AutoCQ first answers those you have not yet worked on the current band in the current mode. It checks the JTDX log and contacts completed during the current session. Your DXCC priorities and queue order apply among new callers. Repeat callers can be answered after them.

## What you need

- **64-bit Windows** with .NET Framework 4.x.
- **JTDX 2.2.160-32A-rc10 Win64** and the patch included in the AutoCQ archive.
- **Node.js LTS** to apply the patch. [Download Node.js](https://nodejs.org/en/download)

AutoCQ can be used with a SunSDR or another radio that already works with JTDX. Keep your audio and rig settings in JTDX. You can use JTDX's built-in log. Logging to LogHX over UDP has also been tested.

To try the interface without JTDX or a radio, unpack AutoCQ and run `JTDX-AutoCQ.exe --demo`. The current app interface is in Russian. The steps below include the button labels you will see.

## Download JTDX 2.2.160-rc10 for Windows

| JTDX build | AutoCQ compatibility |
| --- | --- |
| [32A, 64-bit](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win64.exe.zip) | **Required for AutoCQ** |
| [32A, 32-bit](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-32A-rc10-win32.exe.zip) | For standalone JTDX use |
| [Standard build, 64-bit](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-rc10-win64.exe.zip) | For standalone JTDX use |
| [Standard build, 32-bit](https://ub3dqy.com/downloads/jtdx/2.2.160-rc10/jtdx-2.2.160-rc10-win32.exe.zip) | For standalone JTDX use |

Downloads from EcoAlfa require signing in. The upstream release notes include the archive password and SHA-256 checksums for the extracted installers. [Original release on EcoAlfa](https://ecoalfa.org/recursos/jtdx.4/) · [Release notes and installer SHA-256 checksums](https://ecoalfa.org/recursos/jtdx.4/updates#resource-update-7)

## Install AutoCQ

To upgrade to 0.9.5, close JTDX and AutoCQ. Follow step 3 with the 0.9.5 archive, then open `JTDX-AutoCQ.exe` from that archive. The script updates the JTDX patch using the `jtdx.exe.autocq-original` backup. The patch shipped with 0.9.3 and 0.9.4 also works with 0.9.5.

1. Download the **32A, 64-bit** JTDX build above. Extract the ZIP and check the SHA-256 of `jtdx-2.2.160-32A-rc10-win64.exe` against the table below.
2. Install JTDX and open it once to check your callsign, audio, and rig control. If you use LogHX, point it to this `jtdx.exe` and check that JTDX starts from LogHX. Then close JTDX.
3. Download **JTDX-AutoCQ-…-win64.zip** and check it against `SHA256SUMS.txt` from the same release. Unpack the archive to a folder of its own, open PowerShell in that folder, and run the commands below. If you installed JTDX elsewhere, use its actual path. [Latest AutoCQ release](https://github.com/ub3dqy/jtdx-autocq/releases/latest)

   ```powershell
   Unblock-File -LiteralPath .\Patch-JTDX.ps1
   powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Patch-JTDX.ps1 -JtdxExe "C:\JTDX64\160-32A-rc10\bin\jtdx.exe"
   ```

   The script checks the build and saves the original executable as `jtdx.exe.autocq-original`. If Windows denies write access to the JTDX installation folder, run the commands again from an administrator PowerShell.
4. Open **JTDX-AutoCQ.exe**. For a custom JTDX installation, pass the same path you used to apply the patch.

   ```powershell
   .\JTDX-AutoCQ.exe --jtdx "D:\Radio\JTDX\bin\jtdx.exe"
   ```

## Start a session

1. With JTDX closed, set your session options in AutoCQ. For a first run, try five minutes, two call attempts, two receive periods for the final message, and a search after three unanswered CQs.
2. Click **«1. Подготовить»** (Prepare). Start the patched JTDX. If you use LogHX, start JTDX from its button in LogHX.
3. In JTDX, choose a band and FT8 or FT4. Clear the DX Callsign field to start with CQ. Use AutoTx and AutoSelect for the exchange and leave Single-shot QSO off.
4. Click **«2. Включить автомат»** (Start AutoCQ). If the button is disabled, give JTDX up to 15 seconds to connect. The frequency will appear in AutoCQ when it receives status from JTDX.

When the timer expires, AutoCQ finishes the current contact and answers any available callers in the queue. Click **«СТОП — Halt TX»** to stop transmission immediately.

To pick a station yourself, open **«Станции из левого окна JTDX»** (Stations from the JTDX left pane), select a recent message, and click **«Позвать выбранную»** (Call selected station). To call a station later, enter its callsign in **«Очередь станций»** (Station queue). After the current contact and any stations calling you, AutoCQ calls the entered callsign directly using JTDX's current TX period and audio offset. Entering the callsign is enough to queue the call.

## Monitor targets

1. In AutoCQ, open **«Цели мониторинга»** (Monitoring targets). Choose DXCC entities, enter exact callsigns, or set callsign patterns. For example, `9J*` matches callsigns starting with 9J, and `K?ABC` matches K1ABC and K2ABC.
2. Select **«Режим мониторинга»** (Monitoring mode). Choose a band in JTDX and leave it receiving. Click **«2. Включить мониторинг»** (Start monitoring).
3. AutoCQ calls a target when a recent CQ, 73, or RRR from it appears in the JTDX left pane. It returns to receive after the contact or the configured number of attempts. This mode never sends CQ. When the timer expires, AutoCQ completes the current call and leaves JTDX receiving.

**«Одна связь на цель на диапазоне»** (One contact per target per band) uses your JTDX log. For example, if you select Japan and already have a contact on 20 m, the target is complete on 20 m. It can still be available on 40 m. This option is on by default.

## Return to standard JTDX

Close JTDX first, then AutoCQ. AutoCQ restores the temporary connection and sequencing settings when JTDX closes. You can also click **«Восстановить прежние настройки JTDX»** (Restore previous JTDX settings) while JTDX is closed. Single-shot QSO stays off after preparation.

To restore the original `jtdx.exe`, close JTDX and run this command from the AutoCQ folder.

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Patch-JTDX.ps1 -JtdxExe "C:\JTDX64\160-32A-rc10\bin\jtdx.exe" -Restore
```

## Checksums

Run this command in the folder containing the extracted JTDX installer.

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath .\jtdx-2.2.160-32A-rc10-win64.exe
```

| File | SHA-256 |
| --- | --- |
| `jtdx-2.2.160-32A-rc10-win64.exe` installer | `67276B42561E3D1722BACABBA39B8B467C21BDFB73E4EEBFD832AB6152122574` |
| Original installed `bin/jtdx.exe` | `6B6718432EDB55B3E3C8A5B884522BDC5EF5E466BF074E9DAC09CC0B64CE399C` |
| `bin/jtdx.exe` with the AutoCQ 0.9.3–0.9.5 patch | `B615BFBDC23291A7CA297FE1EAE4EB7D2AF0E8966C962D4FB0EA620C6FF6B12F` |

## Build and test

On Windows, run `.\build.ps1` to build `build/JTDX-AutoCQ.exe`. Use `.\build.ps1 -Test` to run the offline tests. Pass `-JtdxBin` with the path to the patched rc10 `bin` folder to also check the native patch and Qt calls. These tests run without starting JTDX or transmitting.

The patch source is in `patch/queue-reply.asm` and `patch/build-native.cjs`. Use NASM 2.16.03 to rebuild the patch binary.

```powershell
nasm -f bin -o patch/queue-reply.bin patch/queue-reply.asm
```

## Licensing

An AutoCQ license has yet to be specified.

Igor Chernikov UA3DJY and Arvo Järve ES1JA developed JTDX with community contributions. JTDX is distributed under the GNU GPL version 3. [JTDX project](https://github.com/jtdx-project/jtdx) · [GPLv3 license](https://www.gnu.org/licenses/gpl-3.0.html)
