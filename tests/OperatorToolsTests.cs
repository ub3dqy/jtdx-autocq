using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JtdxAutoCq;

static class OperatorToolsTests
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    static string HamlogPage(string call, string points, params string[] rows)
    {
        return "<p>Results of " + call + "</p>" + points
            + "<table class='award-results-table'><tbody>" + String.Join("", rows) + "</tbody></table>";
    }

    static string HamlogRow(string call, string date, string band, string mode, string points, bool accepted = true)
    {
        return "<tr><td><strong>" + call + "</strong></td><td>" + date + "</td><td>" + band + "</td><td>" + mode
            + "</td><td></td><td></td><td>" + points + "</td><td><i class='fas fa-"
            + (accepted ? "check" : "times") + "'></i></td></tr>";
    }

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

        run("HAMLOG counts only accepted rows for the requested callsign, including fractional and empty results", () => {
            DateTime at = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            string total = "<span class='award-dashboard-value'>13.5 / 85</span>";
            string accepted = HamlogRow("R5AV", "5 Oct 2026", "160M", "SSB", "+13.5");
            string unaccepted = HamlogRow("R4CQ", "5 Oct 2026", "20M", "FT8", "+10", false);
            string page = HamlogPage("K1ABC", total, accepted, unaccepted);
            HamlogStanding result = HamlogStandingClient.Parse(page, HamlogAward.Murom, "K1ABC", at);
            Check(result.CheckedUtc == at && result.Points == 13.5m && result.Credits.Count == 1
                && result.Credits.Single().DayUtc == at.Date, "Wrong points, date or accepted QSO");
            Check(HamlogStandingClient.Parse(HamlogPage("K1ABC", ""), HamlogAward.Murom, "K1ABC", at).Points == 0,
                "A valid empty HAMLOG log was rejected");
            foreach (string bad in new[] {page.Replace("K1ABC", "K1ABD"), page.Replace("+13.5", "+12"), page.Replace(" / 85", " / 75")})
            {
                try { HamlogStandingClient.Parse(bad, HamlogAward.Murom, "K1ABC", at); }
                catch (InvalidDataException) { continue; }
                throw new Exception("A mismatched remote result was accepted");
            }
        });

        run("Award points preserve remote credit and count only eligible extra LogHX contacts", () => {
            DateTime at = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            var award = HamlogAward.Murom.Create();
            award.Hamlog = new HamlogStanding { Callsign = "K1ABC", CheckedUtc = at, Points = 45, Required = 85,
                Credits = new List<HamlogCredit> {
                    new HamlogCredit { Call = "LZ1ZF", DayUtc = at.Date, Band = "40m", Mode = "FT8", Points = 10 },
                    new HamlogCredit { Call = "RW9CC", DayUtc = at.Date, Band = "20m", Mode = "SSB", Points = 15 },
                    new HamlogCredit { Call = "R4CQ", DayUtc = at.Date, Band = "80m", Mode = "CW", Points = 20 }
                }
            };
            Func<string, string, string, DateTime, string, LogbookQso> row = (call, band, mode, day, own) =>
                new LogbookQso { Call = call, Band = band, Mode = mode, StartedUtc = day, Station = own };
            var history = new LogbookHistory("example.lhx2", new List<LogbookQso> {
                row("AC1ZF", "40m", "FT4", at, "K1ABC"), // same member, UTC day, band and digital group
                row("RW9CC", "20m", "USB", at, "K1ABC"),
                row("R4CQ", "80m", "CW", at, "K1ABC"),
                row("R5AV", "160m", "USB", at.AddMinutes(-60), "K1ABC"),
                row("R5AV", "160m", "SSB", at, "K1ABC"),
                row("EW1D", "20m", "FT8", at, "K1ABC"),
                row("R4CQ", "40m", "FT8", at, "K1ABD"),
                row("UA9CES", "40m", "FT8", at.AddMonths(-1), "K1ABC"),
                row("K2BBB", "40m", "FT8", at, "K1ABC"),
            });
            var progress = new HamlogPointProgress(award, history, "K1ABC");
            Check(progress.Standing.Points == 45m && progress.ConfirmedMissingFromLogbook == 1
                && progress.Pending.Count == 2 && progress.PendingPoints == 19.5m,
                "Official results were replaced by local counts, or the multipliers and duplicates were wrong");
            Check(progress.CandidatePoints("LZ1ZF", "40m", "FT4", at) == 0
                && progress.CandidatePoints("LZ1ZF", "40m", "FT4", at.Date.AddDays(1)) == 10m
                && progress.CandidatePoints("LZ1ZF", "20m", "FT4", at) == 10m
                && progress.CandidatePoints("LZ1ZF", "40m", "CW", at) == 20m,
                "The band, UTC day or mode-group rule is wrong");
            progress.Record("U4MIR", "160m", "CW", at);
            Check(progress.PendingEnough && !progress.ConfirmedComplete && !progress.Available("40m", "FT8", at)
                && progress.Standing.Points == 45m, "Pending contacts did not pause calls or were marked officially confirmed");
            Check(!new HamlogPointProgress(award, history, "K1ABD").CanSearch, "Used another operator's HAMLOG snapshot");
        });

        run("Award monitoring uses an open point target and stops calling after a logged QSO covers the deficit", () => {
            DateTime at = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            var murom = HamlogAward.Murom.Create();
            murom.Selected = true;
            murom.Hamlog = new HamlogStanding { Callsign = "K1ABC", CheckedUtc = at, Points = 79, Required = 85 };
            var tula = HamlogAward.Tula.Create();
            tula.Selected = true;
            tula.Hamlog = new HamlogStanding { Callsign = "K1ABC", CheckedUtc = at, Points = 1, Required = 50 };
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { murom, tula } };
            var history = new LogbookHistory("example.lhx2", new List<LogbookQso>());
            var engine = new AutoCqEngine();
            var search = new AwardSearch(catalog, history, "K1ABC", "20m", "FT8", call => null);
            Check(search.Available(at) == 1 && search.Score("R875TULA", at) == 0, "The expired program supplied a target");
            var status = new RadioStatus { Frequency = 14074000, Mode = "FT8", DeCall = "K1ABC", DxCall = "" };
            engine.OnStatus(status, at);
            var requests = new List<string>();
            engine.RequestCq = () => requests.Add("CQ");
            engine.RequestReply = decode => requests.Add(decode.Message);
            engine.Start(at, search);
            foreach (string message in new[] { "CQ R875TULA KO84", "CQ R5AV KO85" })
                engine.OnDecode(new DecodedMessage { IsNew = true, Mode = "~", Message = message,
                    Milliseconds = (uint)at.TimeOfDay.TotalMilliseconds }, at.AddSeconds(14));
            engine.Tick(at.AddSeconds(14), true);
            Check(requests.SequenceEqual(new[] { "CQ R5AV KO85" }), "The point target did not reach the normal monitoring selector");
            status.DxCall = "R5AV"; status.TxEnabled = true;
            engine.OnStatus(status, at.AddSeconds(15));
            engine.OnLine("20261005_120015.100(0) Transmitting 14.074 MHz + 1500Hz FT8: R5AV K1ABC RR73", at.AddSeconds(15));
            Check(search.Available(at.AddSeconds(16)) == 1, "A transmission alone advanced the award");
            engine.OnLine("20261005_120015.200(0) QSO logged: R5AV", at.AddSeconds(15.2));
            Check(search.Available(at.AddSeconds(16)) == 1, "An undated log event advanced the points");
            engine.OnLoggedContact(QtPacket.Parse(LoggedQsoPacket("FT8", at, at.AddSeconds(15), 3)).LoggedContact);
            Check(search.Available(at.AddSeconds(16)) == 0 && murom.Hamlog.Points == 79,
                "The monitor kept new targets open or advanced HAMLOG credit without a refresh");
        });

        run("A contact logged after midnight advances the award on its recorded UTC start day", () => {
            DateTime midnight = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
            var program = HamlogAward.Murom.Create();
            program.Selected = true;
            program.Hamlog = new HamlogStanding { Callsign = "K1ABC", CheckedUtc = midnight.AddHours(-1), Points = 20, Required = 85 };
            var history = new LogbookHistory("example.lhx2", new List<LogbookQso>());
            var search = new AwardSearch(new AwardCatalog { Programs = new List<AwardProgram> { program } },
                history, "K1ABC", "20m", "FT4", call => null);
            DateTime started = midnight.AddSeconds(-15), ended = midnight.AddSeconds(7);
            LogbookQso logged = QtPacket.Parse(LoggedQsoPacket("FT4", started, ended, 2, 3 * 3600)).LoggedContact;
            Check(logged != null && logged.Call == "R5AV" && logged.Station == "K1ABC" && logged.Band == "20m"
                && logged.Mode == "FT4" && logged.StartedUtc == started && logged.EndedUtc == ended
                && logged.StartedUtc.Value.Kind == DateTimeKind.Utc,
                "The QSO Logged packet lost the station, band, mode or UTC dates");
            search.Record(logged);
            Check(search.Score("R5AV", midnight.AddSeconds(-1)) == 0
                && search.Score("R5AV", midnight.AddSeconds(20)) == 1, "The prior day's QSO consumed today's award credit");
            using (var packet = new QtWriter(12, "JTDX", 2))
            {
                packet.Text("<CALL:4>R5AV<BAND:3>20m<MODE:4>MFSK<SUBMODE:3>FT4<STATION_CALLSIGN:5>K1ABC"
                    + "<QSO_DATE:8>20261005<TIME_ON:6>235945<QSO_DATE_OFF:8>20261006<TIME_OFF:6>000007<EOR>");
                LogbookQso adif = QtPacket.Parse(packet.Bytes()).LoggedContact;
                Check(adif != null && adif.Mode == "FT4" && adif.StartedUtc == started,
                    "The native ADIF packet lost mode or QSO date");
            }
            foreach (uint type in new[] { 5u, 12u })
                using (var empty = new QtWriter(type, "JTDX", 2))
                {
                    QtPacket parsed = QtPacket.Parse(empty.Bytes());
                    Check(parsed.Type == type && parsed.LoggedContact == null, "The bridge can no longer relay a legacy log packet");
                }
        });

        run("A catalog refresh keeps the local award snapshot and the installed account can reopen it", () => {
            string dir = Path.Combine(Path.GetTempPath(), "AutoCQ-points-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "catalog.json"), prefs = Path.Combine(dir, "preferences.xml");
            try
            {
                var program = HamlogAward.Murom.Create();
                program.Selected = true;
                program.Hamlog = new HamlogStanding { Callsign = "K1ABC", CheckedUtc = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), Points = 6, Required = 85,
                    Credits = new List<HamlogCredit> { new HamlogCredit { Call = "R5AV", Band = "20m", Mode = "FT8", Points = 6 } } };
                var app = new AppPreferences { Awards = new AwardCatalog { Programs = new List<AwardProgram> { program } } };
                File.WriteAllText(file, "{\"version\":1,\"programs\":[{\"url\":\"" + program.Url + "\",\"title\":\"Murom Falcon\"}]}");
                app.Awards.Import(file, "K1ABC");
                var store = new AppPreferencesStore(dir);
                store.Save(app);
                AwardProgram loaded = store.Load().Awards.Programs.Single();
                Check(loaded.UsesPointRules && loaded.Selected && loaded.Hamlog.Credits.Single().Call == "R5AV"
                    && loaded.Hamlog.Points == 6 && loaded.From.Value.Year == 2026, "Catalog refresh or persistence lost the standing");
            }
            finally
            {
                File.Delete(file);
                File.Delete(prefs);
                Directory.Delete(dir);
            }
        });
    }

    static byte[] LoggedQsoPacket(string mode, DateTime started, DateTime ended, uint schema, int offset = 0)
    {
        using (var packet = new QtWriter(5, "JTDX", schema))
        {
            WriteQtDateTime(packet, ended, offset);
            packet.Text("R5AV");
            packet.Text("KO85");
            packet.U64(14074000);
            packet.Text(mode);
            foreach (string field in new[] { "-10", "-11", "", "", "" }) packet.Text(field);
            WriteQtDateTime(packet, started, offset);
            packet.Text("K1ABD");
            packet.Text("K1ABC");
            packet.Text("FN31");
            return packet.Bytes();
        }
    }

    static void WriteQtDateTime(QtWriter packet, DateTime utc, int offset)
    {
        DateTime time = utc.AddSeconds(offset);
        packet.U64((ulong)(2440588 + (time.Date - new DateTime(1970, 1, 1)).Days));
        packet.U32((uint)time.TimeOfDay.TotalMilliseconds);
        packet.Byte(offset == 0 ? (byte)1 : (byte)2);
        if (offset != 0) packet.U32(unchecked((uint)offset));
    }
}
