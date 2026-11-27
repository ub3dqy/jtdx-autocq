using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    public sealed partial class MainForm
    {
        readonly Button pauseAfterQso = new Button();
        readonly SessionTraceBuffer sessionTrace = new SessionTraceBuffer();
        SessionTraceDialog traceDialog;
        DateTime nextAuxiliaryRefresh;

        void UpdateAuxiliaryViews(DateTime now)
        {
            if (now < nextAuxiliaryRefresh) return;
            try
            {
                UpdateListening(now);
                UpdatePropagation(now);
                UpdateDecodeDiagnostics(now);
            }
            catch (Exception error)
            {
                DiagnosticEvent("Сбой при обновлении дополнительного окна. " + error.Message);
                nextAuxiliaryRefresh = now.AddSeconds(5);
            }
        }

        void ChangePause()
        {
            DateTime now = DateTime.UtcNow;
            if (engine.Paused)
            {
                TraceAction(TraceEvent.Resume, "Продолжить сеанс");
                engine.Resume(now);
            }
            else
            {
                TraceAction(TraceEvent.Pause, "Пауза после QSO");
                engine.RequestPause(now);
            }
            UpdateNativeSelection(now);
            UpdateUi();
        }

        void TraceAction(TraceEvent kind, string text, bool connected = false)
        {
            if (sessionTrace.Active) sessionTrace.Add(new TraceFrame { Kind = kind, AtUtc = DateTime.UtcNow,
                Text = text, Connected = connected });
        }

        void TraceCommand(string action, string text = null)
        { TraceAction(TraceEvent.Command, SessionTrace.Command(action, text)); }

        void StopTraced(string reason, bool halt)
        {
            TraceAction(TraceEvent.Stop, reason, halt);
            engine.Stop(reason, halt);
        }

        void ReadRadioLine(string line)
        {
            DateTime now = DateTime.UtcNow;
            if (sessionTrace.Active && line.Length <= 2048)
                sessionTrace.Add(new TraceFrame { Kind = TraceEvent.Native, AtUtc = now, Text = line });
            decodeDiagnostics.Line(line, now);
            engine.OnLine(line, now);
        }

        void ShowSessionTrace()
        {
            if (traceDialog != null && !traceDialog.IsDisposed) { traceDialog.Activate(); return; }
            if (demo) { traceDialog = (SessionTraceDialog)TraceDemoForm(); traceDialog.Show(this); return; }
            DxccCountries table = dxcc;
            try { if (table == null) table = CountryTable(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            traceDialog = new SessionTraceDialog(sessionTrace.Snapshot, preferences.Profiles.ToArray(), call => {
                DxccCountry country = table == null ? null : table.Find(call);
                return country == null ? null : country.Prefix;
            }, () => Guard(() => OpenGuide("session-trace", "разбор-сеанса")));
            traceDialog.Show(this);
        }

        Form TraceDemoForm()
        {
            DateTime at = new DateTime(2026, 1, 15, 18, 30, 0, DateTimeKind.Utc);
            var trace = new SessionTrace { StartUtc = at, OwnCall = "K1ABC", Operation = "Автомат CQ",
                Options = new AutoCqOptions(), FirstStatus = new RadioStatus { DeCall = "K1ABC", Mode = "FT8", Frequency = 14074000, TxFirst = true },
                Frames = new[] {
                    new TraceFrame { AtUtc = at.AddSeconds(1), Kind = TraceEvent.Timer, Connected = true },
                    new TraceFrame { AtUtc = at.AddSeconds(1), Kind = TraceEvent.Command, Text = "Общий вызов" },
                    new TraceFrame { AtUtc = at.AddSeconds(2), Kind = TraceEvent.Native, Text = "20260115_183002.000(0) Transmitting 14.074 MHz + 1500Hz FT8: CQ K1ABC FN20" },
                    new TraceFrame { AtUtc = at.AddSeconds(28), Kind = TraceEvent.Decode, Decode = new DecodedMessage {
                        IsNew = true, Mode = "~", Snr = -10, DeltaFrequency = 1500, Milliseconds = 66615000, Message = "K1ABC W2ABC FN31" } },
                    new TraceFrame { AtUtc = at.AddSeconds(29), Kind = TraceEvent.Decision, Text = "Входящий вызов от W2ABC. Отвечаю станции." },
                    new TraceFrame { AtUtc = at.AddSeconds(29), Kind = TraceEvent.Command, Text = "Ответ вызывающей станции · K1ABC W2ABC FN31" },
                    new TraceFrame { AtUtc = at.AddSeconds(30), Kind = TraceEvent.Native, Text = "20260115_183030.000(0) Transmitting 14.074 MHz + 1500Hz FT8: W2ABC K1ABC -10" },
                    new TraceFrame { AtUtc = at.AddSeconds(90), Kind = TraceEvent.Native, Text = "20260115_183130.000(0) Transmitting 14.074 MHz + 1500Hz FT8: W2ABC K1ABC RR73" },
                    new TraceFrame { AtUtc = at.AddSeconds(119), Kind = TraceEvent.Decision, Text = "Получено заключительное 73 от W2ABC." },
                } };
            return new SessionTraceDialog(() => trace, new OperatorProfile[0], call => null, () => { });
        }
    }
}
