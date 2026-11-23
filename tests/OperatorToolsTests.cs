using System;
using System.Collections.Generic;
using System.IO;
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

        run("A HAMLOG catalog refresh keeps prepared goals and recognizes issuance without sharing operator data", () => {
            string directory = TestWorkspace.Create();
            string file = Path.Combine(directory, "hamlog.json");
            var settings = new AppPreferences();
            File.WriteAllText(file, @"{""version"":1,""callsign"":""K1AAA"",""generatedUtc"":""2026-01-15T12:00:00.000Z"",""programs"":[
                {""title"":""Example award"",""url"":""https://hamlog.online/club/demo/10/"",""hasIssued"":false,
                ""goals"":[{""kind"":""call"",""value"":""ja1aa"",""band"":""20M"",""mode"":""ft8""}]}]}");
            settings.Awards.Import(file, "K1AAA");
            settings.Awards.Programs[0].Selected = true;
            File.WriteAllText(file, @"{""version"":1,""callsign"":""K1AAA"",""generatedUtc"":""2026-01-16T12:00:00Z"",""programs"":[
                {""title"":""Example award"",""url"":""https://hamlog.online/club/demo/10"",""hasIssued"":true},
                {""title"":""New award"",""url"":""https://hamlog.online/club/demo/11/"",""hasIssued"":null}]}");
            settings.Awards.Import(file, "K1AAA");
            var store = new AppPreferencesStore(directory);
            store.Save(settings);
            AwardCatalog loaded = store.Load().Awards;
            AwardProgram issued = loaded.Programs.Single(program => program.HasIssued == true);
            Check(loaded.Programs.Count == 2 && issued.Goals.Single().Value == "JA1AA" && !issued.Selected,
                "Refresh duplicated a program, lost its goals or kept it active after issuance.");
            try { loaded.Import(file, "K1BBB"); throw new Exception("Imported another station's award account."); }
            catch (InvalidOperationException) { }
            Check(loaded.Callsign == "K1AAA", "The rejected import changed the current award account.");
        });

        run("Award monitoring chooses a station benefiting two programs and counts actual logged contacts", () => {
            DateTime at = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
            var catalog = new AwardCatalog { Callsign = "K1AAA", Programs = new List<AwardProgram> {
                new AwardProgram { Title = "Countries", Selected = true, FromUtc = "2026-01-01", Goals = new List<AwardGoal> {
                    new AwardGoal { Kind = "dxcc", Value = "JA", Band = "20m", Mode = "FT8", Count = 2 },
                } },
                new AwardProgram { Title = "Special station", Selected = true, Goals = new List<AwardGoal> {
                    new AwardGoal { Kind = "call", Value = "JA3ZZ", Band = "20m" },
                } },
                new AwardProgram { Title = "FT4", Selected = true, Goals = new List<AwardGoal> {
                    new AwardGoal { Kind = "mask", Value = "JA*", Mode = "FT4" },
                } },
            } };
            foreach (AwardProgram program in catalog.Programs) program.Validate();
            var history = new LogbookHistory("example.lhx2", new List<LogbookQso> {
                new LogbookQso { Call = "JA1AA", Band = "20m", Mode = "FT8", Station = "K1AAA", StartedUtc = at.AddMinutes(-60) },
                new LogbookQso { Call = "JA2AA", Band = "20m", Mode = "FT8", Station = "K1BBB", StartedUtc = at.AddMinutes(-60) },
                new LogbookQso { Call = "JA2AA", Band = "40m", Mode = "FT8", Station = "K1AAA", StartedUtc = at.AddMinutes(-60) },
                new LogbookQso { Call = "JA2AA", Band = "20m", Mode = "FT8", Station = "K1AAA", StartedUtc = at.AddMonths(-1) },
            });
            var engine = new AutoCqEngine { DxccForCall = call => call.StartsWith("JA") ? "JA" : null };
            var search = new AwardSearch(catalog, history, "K1AAA", "20m", "FT8", engine.DxccForCall);
            Check(search.Score("JA1AA", at) == 0 && search.Score("JA2AA", at) == 1 && search.Score("JA3ZZ", at) == 2,
                "The award comparison missed LogHX history, band, time, mode or operator.");
            engine.OnStatus(new RadioStatus { Frequency = 14074000, Mode = "FT8", DeCall = "K1AAA", DxCall = "" }, at);
            var commands = new List<string>();
            engine.RequestCq = () => commands.Add("CQ");
            engine.RequestReply = decode => commands.Add(decode.Message);
            engine.Start(at, search);
            foreach (string text in new[] { "K1AAA W9ZZ FN31", "CQ JA2AA PM95", "CQ JA3ZZ PM96" })
                engine.OnDecode(new DecodedMessage { IsNew = true, Mode = "~", Message = text,
                    Milliseconds = (uint)at.TimeOfDay.TotalMilliseconds }, at.AddSeconds(14));
            engine.Tick(at.AddSeconds(14), true);
            Check(engine.Monitoring && commands.SequenceEqual(new[] { "CQ JA3ZZ PM96" }),
                "Award search started CQ, answered an unrelated caller or ignored the shared goal.");
            engine.OnStatus(new RadioStatus { Frequency = 14074000, Mode = "FT8", DeCall = "K1AAA", DxCall = "JA3ZZ", TxEnabled = true }, at.AddSeconds(15));
            engine.OnLine("20260115_120015.100(0) Transmitting 14.074 MHz + 1500Hz FT8: JA3ZZ K1AAA RR73", at.AddSeconds(15));
            Check(search.Available(at.AddSeconds(16)) == 2, "A terminal transmission was counted as a logged contact.");
            engine.OnLine("20260115_120015.200(0) QSO logged: JA3ZZ", at.AddSeconds(15.2));
            Check(search.Available(at.AddSeconds(16)) == 0 && search.Score("JA3ZZ", at.AddSeconds(16)) == 0,
                "The logged QSO did not retire both goals.");
        });
    }
}
