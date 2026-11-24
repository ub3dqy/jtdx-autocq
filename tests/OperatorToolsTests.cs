using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
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

        run("The HAMLOG catalog includes national, territory and every paginated club award", () => {
            const string html = "<table id='listgn'><tbody>"
                + "<tr><td>Example</td><td><a href='/neca/info.php'>NECA</a></td></tr>"
                + "<tr><td>Example</td><td><a href='#'>Coming soon</a></td></tr>"
                + "<!--<tr><td>Example</td><td><a href='/unlisted/'>Hidden</a></td></tr>-->"
                + "</tbody></table><div id='tcawards-md'><h4>Territory sponsor</h4>"
                + "<table><tr><td><a href='/geo/rafa/'>Territory</a></td><td>01 Jan 2024</td></tr></table></div></fieldset>";
            var offsets = new List<int>();
            HamlogCatalogSnapshot catalog = HamlogCatalogClient.Parse(html, (draw, offset) => {
                offsets.Add(offset);
                return new HamlogCatalogPage { Draw = draw, Total = 101,
                    Rows = Enumerable.Range(offset, Math.Min(100, 101 - offset)).Select(i => new[] {
                        "<a href='/club/demo/'>Club sponsor</a>", "<a href='/club/demo/" + (i + 10) + "/'>Award " + i + "</a>",
                        "05 Oct 2026 - 30 Nov 2026"
                    }).ToArray()
                };
            }, null);
            Check(offsets.SequenceEqual(new[] { 0, 100 }) && catalog.Programs.Count == 104
                && catalog.Programs.Select(row => row.CatalogKey).Distinct().Count() == 104
                && catalog.Programs.Single(row => row.Title == "Territory").Sponsor == "Territory sponsor"
                && catalog.Programs.Single(row => row.Title == "NECA").Url == "https://hamlog.online/neca/"
                && catalog.Programs.Single(row => row.Title == "Award 100").PublishedUntilUtc == "2026-11-30"
                && catalog.Programs.Single(row => row.Title == "Coming soon").Url == "",
                "A section, announcement, valid date or page of awards was lost.");
            try
            {
                HamlogCatalogClient.Parse(html, (draw, offset) => new HamlogCatalogPage { Draw = draw, Total = 101,
                    Rows = new[] { new[] { "", "<a href='/club/demo/1/'>Partial</a>", "" } }
                }, null);
                throw new Exception("A partial catalogue was published to the operator.");
            }
            catch (InvalidDataException) { }
        });

        run("Public catalog and account export merge without losing personal goals or marked issuance", () => {
            var when = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            const string url = "https://hamlog.online/neca/";
            var original = new AwardProgram { Id = "custom-neca", Title = "Personal title", Url = url, Selected = true,
                ManualSearch = true, FromUtc = "2026-11-01", Goals = new List<AwardGoal> {
                    new AwardGoal { Kind = "call", Value = "W2ABC", Band = "20m" }
                }
            };
            original.Validate();
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { original } };
            var publication = new AwardProgram { Id = url, Url = url, Title = "NECA", PublicEntry = true,
                Section = "World", Sponsor = "Example", PublishedFromUtc = "2026-01-01", PublishedUntilUtc = "2026-12-31" };
            publication.Validate();
            var publicCatalog = new HamlogCatalogSnapshot(new[] { publication });
            catalog.ApplyPublicCatalog(publicCatalog, when);
            AwardProgram result = catalog.Programs.Single(row => row.Url == url);
            Check(result.UsesLocalTargets && result.Selected && result.From.Value.Month == 11 && result.Until.Value.Year == 2027
                && result.Id == original.Id && result.HasIssued == null, "The public refresh changed personal data or date restrictions.");

            string directory = TestWorkspace.Create();
            string personal = Path.Combine(directory, "HAMLOG-K1ABC-AutoCQ.json");
            File.WriteAllText(personal, "{\"version\":1,\"callsign\":\"K1ABC\",\"generatedUtc\":\"2026-11-24T12:00:00Z\","
                + "\"programs\":[{\"title\":\"Local name\",\"url\":\"https://hamlog.online/neca/info.php\",\"hasIssued\":true}]}");
            catalog.Import(personal, "K1ABC", true);
            catalog.ApplyPublicCatalog(publicCatalog, when.AddHours(1));
            result = catalog.Programs.Single(row => row.Url == url);
            var store = new AppPreferencesStore(directory);
            store.Save(new AppPreferences { Awards = catalog });
            AwardProgram saved = store.Load().Awards.Programs.Single(row => row.Url == url);
            Check(result.Title == "NECA" && !saved.Selected && saved.ClosedByIssuance && saved.UsesLocalTargets
                && saved.Goals.Single().Value == "W2ABC" && saved.Sponsor == "Example" && catalog.Callsign == "K1ABC",
                "Account sync duplicated the official card or erased its rules, credit or local targets.");
            catalog.ApplyPublicCatalog(new HamlogCatalogSnapshot(new AwardProgram[0]), when.AddHours(2));
            AwardProgram absent = catalog.Programs.Single(row => row.Url == url);
            Check(absent.Withdrawn && absent.ClosedByIssuance && absent.UsesLocalTargets
                && catalog.Programs.Any(row => row.UsesOnlineRules && row.Template == HamlogAward.Russia),
                "A listing change erased saved goals, issuance or a supported built-in award.");
        });

        run("A saved HAMLOG account page marks only proved issuance for the selected callsign", () => {
            string directory = TestWorkspace.Create();
            string file = Path.Combine(directory, "issued.html");
            const string page = "<div id='myawards-md'><table id='id1'><thead><tr>"
                + "<th>#</th><th>Название</th><th>Позывной</th><th>Дата выдачи</th><th>Номер</th><th></th>"
                + "</tr></thead><tbody>"
                + "<tr><td>1</td><td><a href='/club/demo/12/'>First diploma</a></td>"
                + "<td>K1ABC</td><td>24.11.2026</td><td>123</td><td><a href='/dl.php?i=private-fixture'>Download</a></td></tr>"
                + "<tr><td>2</td><td><a href='/global/russia/test/10/'>Group diploma</a></td>"
                + "<td>K1ABC</td><td>24.11.2026</td><td>124</td></tr>"
                + "<tr><td>3</td><td><a href='/club/demo/13/'>Other person's diploma</a></td>"
                + "<td>K1ABD</td><td>24.11.2026</td><td>125</td></tr>"
                + "</tbody></table></div></fieldset>";
            File.WriteAllText(file, page);
            const string first = "https://hamlog.online/club/demo/12/";
            const string group = "https://hamlog.online/global/russia/test/23/";
            const string other = "https://hamlog.online/club/demo/13/";
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> {
                new AwardProgram { Title = "First diploma", Url = first },
                new AwardProgram { Title = "Group diploma", Url = group },
                new AwardProgram { Title = "Other person's diploma", Url = other }
            } };
            foreach (AwardProgram row in catalog.Programs) row.Validate();
            Check(catalog.ImportIssuedPage(file, "K1ABC") == 0, "A direct or uniquely matched group award was skipped.");
            Check(catalog.Programs.Single(row => row.Url == first).HasIssued == true
                && catalog.Programs.Single(row => row.Url == group).HasIssued == true
                && catalog.Programs.Single(row => row.Url == other).HasIssued == null && catalog.Callsign == "K1ABC",
                "The saved page crossed callsigns or treated a missing row as unearned credit.");
            var store = new AppPreferencesStore(directory);
            store.Save(new AppPreferences { Awards = catalog });
            Check(!File.ReadAllText(Path.Combine(directory, "preferences.xml")).Contains("private-fixture"),
                "A certificate download token entered the local settings.");
            try { new AwardCatalog().ImportIssuedPage(file, "K1ABE"); throw new Exception("A different account was accepted."); }
            catch (InvalidDataException) { }
        });

        run("HAMLOG previews published text and PDF without treating a web page as a search rule", () => {
            const string award = "https://hamlog.online/club/demo/10/";
            const string document = "<main><h1>Conditions</h1><p>One QSO with W2ABC.</p>"
                + "<script>var testScript = 1;</script><p>Use 20 m.</p></main>";
            HamlogRules text = HamlogRules.ParseDocument(award + "rules/", Encoding.UTF8.GetBytes(document));
            Check(text.Text.Contains("One QSO with W2ABC.") && text.Text.Contains("Use 20 m.")
                && !text.Text.Contains("testScript") && text.Pdf == null, "HTML rules were not shown as inert text.");
            byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.4 fixture");
            Check(HamlogRules.ParseDocument(award + "rules/", pdf).Pdf.SequenceEqual(pdf), "PDF rules were not passed through.");
            Check(HamlogRules.FindPublishedLink("<a href='https://example.org/rules.pdf'>Rules</a>"
                + "<a href='rules/'>Положение</a>", award) == award + "rules/", "The rules link left the HAMLOG site.");
            var program = new AwardProgram { Title = "Example", Url = award, Goals = new List<AwardGoal> {
                new AwardGoal { Kind = "dxcc", Value = "UA" }
            } };
            program.Validate();
            Check(!program.HasSearchRules, "An old country mask was silently enabled for an unsupported award.");
            program.ManualSearch = true;
            Check(program.HasSearchRules, "An explicitly prepared search list could not be used.");
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
            File.WriteAllText(file, @"{""version"":1,""programs"":[
                {""title"":""Example award"",""url"":""https://hamlog.online/club/demo/10/"",""hasIssued"":false}]}");
            loaded.Import(file, "K1AAA");
            Check(loaded.Programs.Single(program => program.Id == issued.Id).ClosedByIssuance,
                "A file without personal records cleared known issuance.");
            File.WriteAllText(file, @"{""version"":1,""callsign"":""K1AAA"",""programs"":[]}");
            try { loaded.Import(file, "K1BBB"); throw new Exception("Imported another station's award account."); }
            catch (InvalidOperationException) { }
            Check(loaded.Callsign == "K1AAA", "The rejected import changed the current award account.");
        });

        run("HAMLOG filters combine and only eligible rows have editable search checkboxes", () => {
            DateTime now = DateTime.UtcNow;
            var ready = new AwardProgram { Id = "ready", Title = "Available award", Sponsor = "Example Club",
                Section = "Клубные и региональные", Goals = new List<AwardGoal> { new AwardGoal { Kind = "call", Value = "W2ABC" } } };
            AwardProgram complete = ready.Copy(), issued = ready.Copy(), expired = ready.Copy(), upcoming = ready.Copy();
            complete.Id = "complete"; complete.Title = "Completed award"; complete.Goals[0].Value = "W2ABD";
            issued.Id = "issued"; issued.Title = "Issued award"; issued.HasIssued = true;
            expired.Id = "expired"; expired.Title = "Expired award"; expired.UntilUtc = now.Date.AddDays(-2).ToString("yyyy-MM-dd");
            upcoming.Id = "upcoming"; upcoming.Title = "Upcoming award"; upcoming.FromUtc = now.Date.AddDays(2).ToString("yyyy-MM-dd");
            var unknown = new AwardProgram { Id = "unknown", Title = "Award without rules", Sponsor = ready.Sponsor,
                Section = ready.Section, PublicEntry = true, Url = "https://hamlog.online/club/demo/12/" };
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { ready, complete, issued, expired, upcoming, unknown } };
            foreach (AwardProgram program in catalog.Programs) program.Validate();
            complete.Selected = issued.Selected = expired.Selected = upcoming.Selected = true;
            var history = new LogbookHistory("example.adi", new List<LogbookQso> {
                new LogbookQso { Call = "W2ABD", Station = "K1ABC", Band = "20m", Mode = "FT8", StartedUtc = now },
            });
            using (var dialog = new AwardsDialog(catalog, history, "K1ABC", call => null))
            {
                var grid = dialog.Controls.OfType<DataGridView>().Single();
                var state = dialog.Controls.OfType<ComboBox>().Single(box => box.AccessibleName == "Состояние");
                var section = dialog.Controls.OfType<ComboBox>().Single(box => box.AccessibleName == "Раздел");
                var query = dialog.Controls.OfType<TextBox>().Single(box => box.AccessibleName == "Название или клуб");
                Check(dialog.Controls.OfType<Button>().Count() == 3, "The award list gained more than three main buttons.");
                Func<string[]> visible = () => grid.Rows.Cast<DataGridViewRow>().Select(row => ((AwardProgram)row.Tag).Id).ToArray();
                Func<string, DataGridViewCell> checkCell = id => grid.Rows.Cast<DataGridViewRow>()
                    .Single(row => ((AwardProgram)row.Tag).Id == id).Cells[0];
                Check(dialog.Catalog.Programs.Any(program => program.Url == HamlogAward.Russia.Url),
                    "Built-in awards still require the separate menu.");
                section.SelectedItem = ready.Section;
                query.Text = "example club";
                Check(visible().SequenceEqual(new[] { "ready", "unknown" }), "The active list did not filter by section and sponsor.");
                state.SelectedItem = "Доступны для поиска";
                Check(visible().SequenceEqual(new[] { "ready" }), "An award without rules became selectable.");
                Check(checkCell("ready") is DataGridViewCheckBoxCell && !checkCell("ready").ReadOnly, "The active field is disabled.");
                checkCell("ready").Value = true;
                state.SelectedItem = "Выполненные и выданные";
                Check(visible().SequenceEqual(new[] { "complete", "issued" }), "The completed filter lost a known result.");
                state.SelectedItem = "Срок истёк";
                Check(visible().SequenceEqual(new[] { "expired" }), "The expired filter returned the wrong awards.");
                state.SelectedItem = "Все дипломы";
                foreach (string inactive in new[] { "complete", "issued", "expired", "upcoming", "unknown" })
                {
                    DataGridViewCell cell = checkCell(inactive);
                    Check(cell.ReadOnly && !(cell is DataGridViewCheckBoxCell) && Object.Equals(cell.Value, "—"),
                        "An inactive award has an editable selection field.");
                }
                Check(dialog.Catalog.Programs.Where(program => program.Selected).Select(program => program.Id).SequenceEqual(new[] { "ready" })
                    && Object.Equals(checkCell("ready").Value, true), "Changing filters lost an eligible choice or kept a stale choice.");
            }
        });

        run("Award monitoring skips known completion and respects an exact published deadline", () => {
            DateTime end = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var limited = HamlogAward.Gunsmiths.Create();
            limited.PublishedUntilUtc = "2026-11-24T12:00:00Z";
            limited.Hamlog = new HamlogStanding { Callsign = "K1ABC", CheckedUtc = end.AddDays(-1), Required = 100 };
            var complete = HamlogAward.Arck35.Create();
            complete.Hamlog = new HamlogStanding { Callsign = "K1ABC", CheckedUtc = end.AddDays(-1), Required = 35, Points = 35 };
            limited.Selected = complete.Selected = true;
            var search = new AwardSearch(new AwardCatalog { Programs = new List<AwardProgram> { limited, complete } },
                LogbookHistory.Empty, "K1ABC", "80m", "FT8", call => "UA");
            Check(search.Score("R3PAA", end.AddSeconds(-1)) == 1 && search.Score("DL2YCK", end.AddSeconds(-1)) == 0,
                "A completed award contributed a target, or the deadline closed the remaining award early.");
            Check(search.Available(end) == 0 && search.Score("R3PAA", end) == 0,
                "A cached selection still contributed targets after the exact expiration time.");
        });

        run("Downloaded club rules create award targets from the published points, dates and repeats", () => {
            const string url = "https://hamlog.online/club/nebo/19/";
            const string document = "Связи на диплом засчитываются с 14 февраля 2026 г. Срок действия диплома — постоянный. "
                + "Засчитываются радиосвязи с радиолюбителями всех стран мира на диапазонах 1,8 - 28 MHz и УКВ. "
                + "Необходимо набрать 45 очков За радиосвязи с Почётными членами клуба «Рыцари неба» R2AKN (R3A-014), "
                + "а также специальным позывным W2ABC начисляется 15 очков. За QSO c членами клуба «Рыцари неба» начисляется 7 очков. "
                + "Для перечисленных выше корреспондентов вводится множитель для следующих видов излучения: "
                + "CW -2 SSB-1,5 DIGI — 1 За QSO на диапазоне 160 метров — 1,5 "
                + "Повторные связи с одним и тем же членом клуба в одни сутки засчитываются на разных диапазонах, "
                + "а на одном — разными видами излучения (все DIGI считаются за один вид). Диплом выдаётся.";
            HamlogRulePlan plan = HamlogPlanCompiler.Compile(url, document);
            Check(plan != null && plan.Required == 45 && plan.MemberPoints == 7 && plan.ExtraPoints == 15
                && plan.FromUtc == "2026-02-14" && plan.Calls.SequenceEqual(new[] { "R2AKN", "W2ABC" }),
                "The compiler lost a published detail or treated a district code as a callsign.");
            Check(HamlogPlanCompiler.Compile(url, document.Replace("DIGI — 1", "DIGI — 3")) == null,
                "A different scoring rule was guessed.");
            DateTime at = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var program = new AwardProgram { Title = "Example", Url = url, DownloadedRules = plan,
                Hamlog = new HamlogStanding { Callsign = "K1ABC", CheckedUtc = at, Required = 45 } };
            program.Validate();
            var history = new LogbookHistory("example.adi", new List<LogbookQso> {
                new LogbookQso { Call = "W2ABC", Station = "K1ABC", Band = "20m", Mode = "FT8", StartedUtc = at }
            });
            var progress = new AwardProgress(program, history, "K1ABC", call => null);
            Check(progress.Points.CandidatePoints("W2ABC", "160m", "FT8", at) == 22.5m
                && progress.Points.CandidatePoints("RA3RPW", "20m", "FT8", at) == 7m,
                "The downloaded plan used a hardcoded bonus or club score.");
            Check(!progress.Needs("W2ABC", "20m", "FT4", at) && progress.Needs("W2ABC", "20m", "FT4", at.AddDays(1))
                && !progress.Needs("W2ABD", "20m", "FT8", at), "An ineligible call or duplicate digital QSO became a target.");
        });

        run("Catalog refresh and re-reading conditions preserve an edited rule and keep the new original", () => {
            string url = "https://hamlog.online/club/nebo/29/";
            Func<int, HamlogRulePlan> plan = points => new HamlogRulePlan {
                Kind = "sky-hunter", Required = 95, MemberPoints = points, ExtraPoints = 20, Calls = new[] { "W2ABC" }
            };
            var saved = new AppPreferences();
            var award = new AwardProgram { Title = "Old", Url = url, PublicEntry = true,
                DownloadedRules = plan(10), EditedRules = plan(11) };
            award.Validate();
            saved.Awards.Programs.Add(award);
            var newEntry = new AwardProgram { Title = "Updated", Url = url, PublicEntry = true };
            newEntry.Validate();
            DateTime now = DateTime.UtcNow;
            saved.Awards.ApplyPublicCatalog(new HamlogCatalogSnapshot(new[] { newEntry }), now);
            AwardProgram refreshed = saved.Awards.Programs.Single(item => item.Url == url);
            refreshed.ApplyDocument(new HamlogRuleResult { Status = "downloaded", CheckedUtc = now, Plan = plan(12) });
            var storage = new AppPreferencesStore(TestWorkspace.Create());
            storage.Save(saved);
            AwardProgram loaded = storage.Load().Awards.Programs.Single(item => item.Url == url);
            Check(loaded.Title == "Updated" && loaded.HasEditedRules && loaded.EffectiveRules.MemberPoints == 11
                && loaded.DownloadedRules.MemberPoints == 12, "A catalog refresh or restart replaced a manual edit.");
            using (var editor = new HamlogPlanDialog(loaded))
            {
                const System.Reflection.BindingFlags access = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var member = (NumericUpDown)typeof(HamlogPlanDialog).GetField("member", access).GetValue(editor);
                Check(member.Value == 11, "The editor did not display the active manual values.");
                member.Value = 13;
                typeof(HamlogPlanDialog).GetMethod("Save", access).Invoke(editor, null);
                Check(editor.Edited.MemberPoints == 13 && loaded.EditedRules.MemberPoints == 11, "The editor did not return an independent edit.");
                typeof(HamlogPlanDialog).GetMethod("LoadValues", access).Invoke(editor, new object[] { loaded.DownloadedRules, false });
                Check(member.Value == 12, "Restore did not display the original values.");
                typeof(HamlogPlanDialog).GetMethod("Save", access).Invoke(editor, null);
                loaded.EditedRules = editor.Edited;
            }
            Check(loaded.EffectiveRules.MemberPoints == 12 && !loaded.HasEditedRules, "Reverting did not restore the downloaded rules.");
            saved.Awards.Programs.Single(item => item.Url == url).EditedRules = plan(11);
            using (var dialog = new AwardsDialog(saved.Awards, LogbookHistory.Empty, "K1ABC", call => null))
            {
                typeof(AwardsDialog).GetMethod("ShowPreservedEdits", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic).Invoke(dialog, null);
                string notice = dialog.Controls.OfType<TextBox>().Single(box => box.Multiline).Text;
                Check(notice.Contains("Ручные правки сохранены") && notice.Contains("Updated"),
                    "The update did not list the awards with saved manual edits.");
            }
        });

        run("HAMLOG rules retry a timeout, record the rate limit and resume from saved documents", () => {
            DateTime now = DateTime.UtcNow;
            var catalog = new AwardCatalog { PublicCatalogUtc = now.AddMinutes(-15) };
            foreach (string id in new[] { "1", "2", "3", "4" })
            {
                var program = new AwardProgram { Id = id, Title = "Document " + id,
                    Url = "https://hamlog.online/club/demo/" + id + "/", PublicEntry = true };
                program.Validate();
                catalog.Programs.Add(program);
            }
            string folder = TestWorkspace.Create();
            var attempts = new Dictionary<string, int>();
            var delays = new List<int>();
            var reports = new List<HamlogRuleResult>();
            bool limited = true;
            var cache = new HamlogRuleCache(folder, (url, cancel) => {
                string id = new Uri(url).Segments.Last().TrimEnd('/');
                if (!attempts.ContainsKey(id)) attempts[id] = 0;
                if (++attempts[id] == 1 && id == "1")
                    throw new HamlogRequestError("Timeout", true);
                if (limited && id == "2")
                    throw new HamlogRequestError("HTTP 429", true, 429, TimeSpan.FromSeconds(60));
                return new HamlogRules(url + "rules/", "A saved public document.", null);
            }, (delay, cancel) => delays.Add(delay));
            Action<HamlogRuleResult> record = result => {
                reports.Add(result);
                catalog.Programs.Single(program => program.Id == result.Id).ApplyDocument(result);
            };
            try
            {
                cache.Scan(catalog.Programs, System.Threading.CancellationToken.None, record);
                throw new Exception("The service pause was ignored.");
            }
            catch (IOException error)
            {
                Check(error.Message.Contains("HTTP 429"), "Unexpected stopped-download error: " + error.Message);
            }
            Check(attempts["1"] == 2 && delays.Contains(2000) && attempts["2"] == 1 && !attempts.ContainsKey("3"),
                "The scan did not retry the temporary failure or continued during the rate limit.");
            Check(reports.Count == 2 && reports[0].Status == "downloaded" && reports[1].Error == "HTTP 429"
                && HamlogRuleCache.CanResume(catalog, now), "Partial progress or the document-specific failure was lost.");
            limited = false;
            cache.Scan(catalog.Programs, System.Threading.CancellationToken.None, record);
            Check(attempts["1"] == 2 && attempts["3"] == 1 && attempts["4"] == 1
                && reports.Skip(2).All(report => report.Status == "downloaded") && !HamlogRuleCache.CanResume(catalog, now),
                "Resuming redownloaded the cached file, skipped a document or left the whole scan pending.");
        });

        run("An unavailable HAMLOG document leaves later documents in the queue", () => {
            var expired = new AwardProgram { Id = "old", Title = "Old", Url = "https://hamlog.online/club/demo/12/",
                PublicEntry = true, PublishedUntilUtc = "2026-01-01" };
            var next = new AwardProgram { Id = "next", Title = "Next", Url = "https://hamlog.online/club/demo/13/",
                PublicEntry = true };
            var reports = new List<HamlogRuleResult>();
            int denied = 0;
            var cache = new HamlogRuleCache(TestWorkspace.Create(), (url, cancel) => {
                if (url == expired.Url)
                {
                    denied++;
                    throw new HamlogRequestError("HTTP 403", false, 403);
                }
                return new HamlogRules(url + "rules/", "Published requirements.", null);
            }, (delay, cancel) => { });
            cache.Scan(new[] { expired, next }, System.Threading.CancellationToken.None, reports.Add);
            Check(denied == 1 && reports.Count == 2 && reports[0].Error == "HTTP 403" && reports[1].Status == "downloaded",
                "A permanently unavailable document was retried or stopped the remaining download.");
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
            foreach (string text in new[] { "CQ JA2AA PM95", "CQ JA3ZZ PM96" })
                engine.OnDecode(new DecodedMessage { IsNew = true, Mode = "~", Message = text,
                    Milliseconds = (uint)at.TimeOfDay.TotalMilliseconds }, at.AddSeconds(14));
            engine.Tick(at.AddSeconds(14), true);
            Check(engine.Monitoring && commands.SequenceEqual(new[] { "CQ JA3ZZ PM96" }),
                "Award search started CQ or ignored the shared goal.");
            engine.OnStatus(new RadioStatus { Frequency = 14074000, Mode = "FT8", DeCall = "K1AAA", DxCall = "JA3ZZ", TxEnabled = true }, at.AddSeconds(15));
            engine.OnLine("20260115_120015.100(0) Transmitting 14.074 MHz + 1500Hz FT8: JA3ZZ K1AAA RR73", at.AddSeconds(15));
            Check(search.Available(at.AddSeconds(16)) == 2, "A terminal transmission was counted as a logged contact.");
            engine.OnLine("20260115_120015.200(0) QSO logged: JA3ZZ", at.AddSeconds(15.2));
            Check(search.Available(at.AddSeconds(16)) == 0 && search.Score("JA3ZZ", at.AddSeconds(16)) == 0,
                "The logged QSO did not retire both goals.");
        });

        run("My Russia accepts only an uncredited live square and reserves a new QSO for the session", () => {
            DateTime at = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            const string hunter = @"<section class=""rrnars-award-group""><h5>Hunter</h5>
                <div class=""small mb-2"">2 / 100</div><div class=""small mb-2"">2 / 750</div></section>";
            const string squares = @"<button data-locator=""KO85"" data-status=""cfm""></button>
                <button data-locator=""LO36"" data-status=""both""></button>
                <button data-locator=""LO44"" data-status=""act""></button>";
            var program = HamlogAward.Russia.Create();
            program.HasIssued = true; // A past issuance does not close the search for the next tier.
            program.Selected = true;
            program.Hamlog = HamlogStandingClient.Parse(HamlogPage("K1AAA", hunter + squares),
                HamlogAward.Russia, "K1AAA", at);
            Check(program.Hamlog.Points == 2 && program.Hamlog.Required == 100
                && program.Hamlog.ConfirmedSquares.SequenceEqual(new[] { "KO85", "LO36" }),
                "Activation-only squares were counted, or the next tier was lost.");
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { program } };
            catalog.NormalizeOnlinePrograms();
            var e = new AutoCqEngine { DxccForCall = call => call.StartsWith("R") ? "UA" : "K" };
            var search = new AwardSearch(catalog, LogbookHistory.Empty, "K1AAA", "20m", "FT8", e.DxccForCall);
            Check(search.Score("R3PAA", at) == 0 && search.Score("R3PAA", at, "KO85AX") == 0
                && search.Score("R3PAA", at, "RR73") == 0 && search.Score("W3AAA", at, "LO44") == 0,
                "Missing, confirmed, malformed or foreign squares became a goal.");
            e.OnStatus(new RadioStatus { Frequency = 14074000, Mode = "FT8", DeCall = "K1AAA", DxCall = "" }, at);
            var requests = new List<string>();
            e.RequestReply = d => requests.Add(d.Message);
            e.Start(at, search);
            foreach (string message in new[] { "CQ R3PAA KO85", "W3AAA R4GBB RR73", "CQ R3NAA LO44" })
                e.OnDecode(new DecodedMessage { IsNew = true, Mode = "~", Message = message,
                    Milliseconds = (uint)at.TimeOfDay.TotalMilliseconds }, at.AddSeconds(14));
            e.Tick(at.AddSeconds(14), true);
            Check(requests.SequenceEqual(new[] { "CQ R3NAA LO44" }), "Search targeted a credited or unknown square.");
            e.OnLoggedContact(new LogbookQso { Call = "R3NAA", Station = "K1AAA", Band = "20m", Mode = "FT8",
                StartedUtc = at.AddSeconds(15), EndedUtc = at.AddSeconds(45) });
            Check(search.Score("R4GCC", at.AddSeconds(45), "LO44") == 0 && program.Hamlog.Points == 2
                && search.Score("R3NAA", at.AddSeconds(45), "LO45") == 1,
                "A logged square was not reserved, was credited prematurely, or the call was blocked in a new square.");
            Check(e.Cqs.LocatorFor("R3NAA", at.AddMinutes(3)) == null, "A stale locator was reused.");
        });

        run("HAMLOG regions allow a repeat on another band while grouping FT8 and FT4 together", () => {
            DateTime at = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var program = HamlogAward.Gunsmiths.Create();
            program.Selected = true;
            program.Goals.Add(new AwardGoal { Kind = "dxcc", Value = "UA" }); // An old broad search rule.
            program.Hamlog = new HamlogStanding { Callsign = "K1AAA", CheckedUtc = at, Points = 1, Required = 100,
                Credits = new List<HamlogCredit> {
                    new HamlogCredit { Call = "R3PAA", Band = "40m", Mode = "FT8", DayUtc = at.Date, Points = 1 },
                }
            };
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { program } };
            catalog.NormalizeOnlinePrograms();
            Func<string, string> dxcc = call => "UA";
            var on40 = new AwardSearch(catalog, LogbookHistory.Empty, "K1AAA", "40m", "FT4", dxcc);
            var on80 = new AwardSearch(catalog, LogbookHistory.Empty, "K1AAA", "80m", "FT8", dxcc);
            Check(on40.Score("R3PAA", at) == 0 && on80.Score("R3PAA", at) == 1
                && on80.Score("R3AAA", at) == 0, "A repeat used the wrong band or searched outside the published region.");
            var unverified = catalog.Copy();
            unverified.Programs[0].Hamlog = null;
            Check(new AwardSearch(unverified, LogbookHistory.Empty, "K1AAA", "80m", "FT8", dxcc).Available(at) == 0,
                "Without HAMLOG credit an old prefix or country rule became eligible.");
        });

        run("Bryansk searches for a known new digital district instead of repeating a credited district", () => {
            DateTime at = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var program = HamlogAward.Bryansk.Create();
            program.Hamlog = new HamlogStanding { Callsign = "K1AAA", CheckedUtc = at, Required = 27, Points = 2,
                Credits = new List<HamlogCredit> {
                    new HamlogCredit { Call = "R3YAA", Band = "40m", Mode = "FT8", County = "BR-01", Locator = "KO73" },
                    new HamlogCredit { Call = "R3YBB", Band = "40m", Mode = "CW", County = "BR-02", Locator = "KO83" },
                }
            };
            var result = new HamlogOnlineProgress(program, LogbookHistory.Empty, "K1AAA", call => "UA");
            Check(result.Confirmed == 1 && !result.Needs("R3YAA", "80m", "FT8", at, "KO73")
                && result.Needs("R3YBB", "80m", "FT8", at, "KO83") && !result.Needs("R3YCC", "80m", "FT8", at, "KO83"),
                "An ineligible or unknown district was targeted, or a CW credit blocked a digital contact.");
        });

        run("ARCK 35 qualifies only club members and counts each once across bands", () => {
            DateTime at = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var program = HamlogAward.Arck35.Create();
            program.Hamlog = HamlogStandingClient.Parse(
                HamlogPage("K1ABC", "<span class='award-dashboard-value'>1 / 35</span>",
                    HamlogRow("4K2GG", "23 Nov 2026", "40M", "FT8", "+1")), HamlogAward.Arck35, "K1ABC", at);
            var progress = new HamlogOnlineProgress(program, LogbookHistory.Empty, "K1ABC", call => null);
            Check(!progress.Needs("4K2GG", "20m", "FT8", at, null) && progress.Needs("DL2YCK", "20m", "FT8", at, null)
                && !progress.Needs("K9ZZZ", "20m", "FT8", at, null), "The club or distinct-callsign rule was ignored.");
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
