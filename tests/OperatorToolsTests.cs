using System;
using System.Collections.Generic;
using System.Linq;
using JtdxAutoCq;

static class OperatorToolsTests
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    internal static void RunAll(Action<string, Action> run)
    {
        run("Full ADIF history counts contacts from other programs on the selected band", () => {
            var records = JtdxAdif.ContactsFromText(
                "<CALL:5>JA1AA<BAND:3>40m<MODE:3>SSB<QSO_DATE:8>20260115<TIME_ON:4>1218<EOR>" +
                "<CALL:5>W1AAA<FREQ:6>14.074<MODE:4>MFSK<SUBMODE:3>FT4<EOR>" +
                "<CALL:5>PY1AA<BAND:3>40m<SWL:1>Y<EOR>" +
                "<CALL:5>DL1AA<BAND:3>40m<MODE:3>FT8<STATION_CALLSIGN:5>K1BBB<EOR>");
            var history = new LogbookHistory("full.adi", records);
            Check(records.Count == 3 && records[0].StartedUtc.Value.Kind == DateTimeKind.Utc,
                "The reader included an SWL record or lost UTC.");
            Check(history.WorkedCalls("40m", "K1AAA").SetEquals(new[] { "JA1AA" }),
                "The target history omitted the SSB contact or included a different operator.");
            Check(history.WorkedCalls("40m", "K1AAA", "FT8").Count == 0
                && history.WorkedCalls("20m", "K1AAA", "FT4").Contains("W1AAA"),
                "Caller history ignored the band or the ADIF submode.");
        });

        run("Session matching uses the main log and counts a record only once for two contacts", () => {
            DateTime at = new DateTime(2026, 1, 15, 12, 20, 30, DateTimeKind.Utc);
            var session = new OperatingSession { Band = "40m", Mode = "FT8", OwnCall = "K1AAA", StartedUtc = at.AddMinutes(-2) };
            var initial = new SessionContact { Call = "JA1AA", LoggedUtc = at, ConfirmedUtc = at.AddSeconds(15) };
            session.Contacts.Add(initial);
            session.Contacts.Add(new SessionContact { Call = "JA1AA", LoggedUtc = at.AddSeconds(38) });
            var history = new LogbookHistory("example.lhx2", LogbookHistory.ReadLogHxRows(
                "JA1AA\t40m\tFT8\tK1AAA\t20260115\t122000\t20260115\t122000\n" +
                "JA1AA\t20m\tFT8\tK1AAA\t20260115\t122100\t20260115\t122100\n" +
                "JA1AA\t40m\tFT8\tK9AAA\t20260115\t122100\t20260115\t122100\n"));
            Check(session.FindIn(history).SetEquals(new[] { initial }), "A repeated contact reused a log row or ignored band or station.");
            var copy = session.Snapshot();
            session.Contacts[0].ConfirmedUtc = null;
            Check(copy.Contacts[0].ConfirmedUtc.HasValue, "A displayed summary changed when the live session advanced.");
        });

        run("Profiles, main log and completed session survive a restart with existing preferences", () => {
            var store = new AppPreferencesStore(TestWorkspace.Create());
            var settings = new AutoCqOptions { RunMinutes = 90, MonitorEnabled = true, MonitorDxcc = new[] { "JA" } };
            var profile = new OperatorProfile { Name = "Диплом", Options = settings.Copy() };
            var stored = new AppPreferences {
                JtdxPath = @"C:\Radio\bin\jtdx.exe", AutomaticUpdates = false,
                LogbookPath = @"C:\Radio\Log\full.lhx2", Profiles = new List<OperatorProfile> { profile },
                LastSession = new OperatingSession { Mode = "FT8", StopReason = "Остановлено оператором." },
            };
            settings.MonitorDxcc[0] = "PY";
            store.Save(stored);
            AppPreferences loaded = store.Load();
            Check(loaded.LogbookPath == stored.LogbookPath && loaded.LastSession.StopReason == stored.LastSession.StopReason
                && loaded.JtdxPath == stored.JtdxPath && !loaded.AutomaticUpdates, "Preferences were lost or overwritten.");
            Check(loaded.Profiles.Single().Options.MonitorDxcc.SequenceEqual(new[] { "JA" }),
                "A saved profile was modified by later edits to the working options.");
        });

        run("A worked country and an active cooldown explain why a monitored station was skipped", () => {
            DateTime now = new DateTime(2026, 1, 15, 12, 20, 0, DateTimeKind.Utc);
            var engine = new AutoCqEngine();
            engine.Options.MonitorEnabled = true;
            engine.Options.MonitorDxcc = new[] { "JA" };
            engine.DxccForCall = call => call.StartsWith("JA") ? "JA" : null;
            engine.WorkedCallsOnBand = (band, own) => new[] { "JA2AA" };
            engine.OnStatus(new RadioStatus { Frequency = 7074000, Mode = "FT8", DeCall = "K1AAA", DxCall = "" }, now);
            engine.Start(now);
            engine.Cqs.Observe(new DecodedMessage { IsNew = true, Milliseconds = (uint)now.TimeOfDay.TotalMilliseconds,
                Mode = "~", Message = "CQ JA1AA PM95" }, engine.Status, now);
            Check(engine.ExplainCq("JA1AA", now).Contains("Цель уже выполнена"), "The reason ignored primary history.");
            engine.Cqs.Failed("JA1AA", now, true);
            Check(engine.ExplainCq("JA1AA", now).Contains("Пауза после вызова"), "A timed pause was not explained.");
        });
    }
}
