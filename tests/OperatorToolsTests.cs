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

    static string HamlogRow(string call, string date, string band, string mode, string points, bool accepted = true,
        string county = "", string grid = "")
    {
        return "<tr><td><strong>" + call + "</strong></td><td>" + date + "</td><td>" + band + "</td><td>" + mode
            + "</td><td>" + county + "</td><td>" + grid + "</td><td>" + points + "</td><td><i class='fas fa-"
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

            string withGeography = document.Replace("Для перечисленных",
                "За радиосвязи со специальным временным позывным W2CCC начисляется – 12 очков. "
                + "За QSO с радиолюбителями Алтайского края начисляется – 4 очка. "
                + "За QSO с радиолюбителями КНР начисляется – 5 очков. Для перечисленных");
            HamlogRulePlan regions = HamlogPlanCompiler.Compile(url, withGeography);
            Check(regions != null && regions.BonusGroups.Length == 3, "Extra stations and regions had no rules.");
            Check(HamlogPlanCompiler.Compile(url, withGeography.Replace("Алтайского края", "неизвестной области")) == null,
                "An unknown geographic condition was silently discarded.");
            program.DownloadedRules = regions;
            var geographic = new AwardProgress(program, history, "K1ABC", call =>
                call.StartsWith("R", StringComparison.Ordinal) ? "UA9" : call.StartsWith("B", StringComparison.Ordinal) ? "BY" : "K");
            Check(geographic.Points.CandidatePoints("R9YAA", "20m", "FT8", at) == 4m
                && geographic.Points.CandidatePoints("BA1AA", "160m", "FT4", at) == 7.5m
                && geographic.Points.CandidatePoints("W2CCC", "20m", "FT8", at) == 12m
                && !geographic.Needs("R9YAA/3", "20m", "FT8", at) && !geographic.Needs("R9AAA", "20m", "FT8", at),
                "The rules ignored a point group or inferred the wrong area from a callsign.");
        });

        run("RCPW uses public club calls, the HAMLOG threshold and the published boundary between rates", () => {
            const string url = "https://hamlog.online/club/rcpw/19/";
            const string rules = "Для получения диплома соискателям необходимо набрать 90 очков "
                + "за работу с членами клуба RCPW на любых разрешённых диапазонах любыми видами модуляции (все виды DIGI считаются за один). "
                + "А для радиолюбителей нулевого района и находящихся за пределами России необходимо набрать 45 очков. "
                + "Любая радиосвязь с членом RCPW в дни активности (с 00:00 UTC 15.10.2026 г. по 23:59 UTC 18.10.2026 г.) даёт 9 очков. "
                + "По окончании дней активности (с 19.10.2026 г.) — 4 очка. "
                + "Повторные связи засчитываются на разных КВ (УКВ) диапазонах, на одном диапазоне — разными видами модуляции (все виды DIGI считаются за один). "
                + "Дата начала действия Положения о дипломе — 00:00 UTC 15 октября 2026 г. Время действия — бессрочный.";
            HamlogRulePlan plan = HamlogPlanCompiler.Compile(url, rules);
            Check(plan != null && plan.Rcpw && plan.FromUtc == "2026-10-15" && plan.BonusUntilUtc == "2026-10-19"
                && HamlogPlanCompiler.Compile(url, rules.Replace("19.10.2026", "20.10.2026")) == null,
                "The compiler missed the exact transition or accepted a gap in the conditions.");
            string[] members = HamlogRcpwRoster.Parse("<table><tr><td><p>#23</p></td><td>R3AAA</td><td>Operator</td>"
                + "<td>Previous R3BBB</td></tr><tr><td>44</td><td>R3CCC</td><td>Operator (SK)</td></tr></table>");
            Check(members.SequenceEqual(new[] { "R3AAA" }), "The public roster imported a previous or inactive callsign.");
            DateTime boundary = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
            var program = new AwardProgram { Title = "RCPW example", Url = url, DownloadedRules = plan };
            program.Validate();
            string summary = "<span class='award-dashboard-value'>9 / 45</span>";
            HamlogStanding standing = HamlogStandingClient.Parse(
                HamlogPage("K1ABC", summary, HamlogRow("R3AAA", "15 Oct 2026", "80m", "FT8", "9")),
                program.Template, "K1ABC", boundary);
            standing.RcpwMembers = members;
            program.Hamlog = standing;
            var history = new LogbookHistory("example.adi", new List<LogbookQso>());
            var progress = new AwardProgress(program, history, "K1ABC", call => "UA");
            Check(progress.Points.Required == 45 && progress.Points.CandidatePoints("R3AAA", "40m", "FT8", boundary.AddSeconds(-1)) == 9
                && progress.Points.CandidatePoints("R3AAA", "40m", "FT8", boundary) == 4,
                "The candidate used the wrong threshold or rate.");
            Check(!progress.Needs("R3AAA", "80m", "FT4", boundary.AddDays(1))
                && !progress.Needs("R3BBB", "20m", "FT8", boundary),
                "The next day made a duplicate eligible, or an unknown station entered the search.");
            HamlogStanding empty = HamlogStandingClient.Parse(HamlogPage("K1ABC", ""), program.Template, "K1ABC", boundary);
            Check(empty.Points == 0 && empty.Required == 45 && empty.ProvisionalRequired,
                "An empty HAMLOG result prevented starting the search or was treated as a verified threshold.");
            bool mismatch = false;
            try
            {
                HamlogStandingClient.Parse(HamlogPage("K1ABC", "<span class='award-dashboard-value'>4 / 45</span>",
                    HamlogRow("R3AAA", "15 Oct 2026", "80m", "FT8", "4")), program.Template, "K1ABC", boundary);
            }
            catch (InvalidDataException) { mismatch = true; }
            Check(mismatch, "The score in HAMLOG contradicted the compiled rules without blocking automatic calls.");
        });

        run("New rule formats re-use locally downloaded documents before trying missing files", () => {
            DateTime now = DateTime.UtcNow;
            string folder = TestWorkspace.Create();
            var cached = new AwardProgram { Title = "Cached", Url = "https://hamlog.online/club/nebo/7/", PublicEntry = true };
            var missing = new AwardProgram { Title = "Missing", Url = "https://hamlog.online/club/nebo/8/", PublicEntry = true };
            cached.Validate(); missing.Validate();
            new HamlogRuleCache(folder, (url, cancel) => new HamlogRules(url, "Sample", null), (delay, cancel) => { })
                .Scan(new[] { cached }, default(System.Threading.CancellationToken), cached.ApplyDocument);
            cached.RuleCompilerVersion = HamlogPlanCompiler.Version - 1;
            File.SetLastWriteTimeUtc(Directory.GetFiles(folder).Single(), now.AddDays(-14));
            var results = new List<string>();
            var cache = new HamlogRuleCache(folder, (url, cancel) => {
                Check(url == missing.Url, "Re-downloaded a document just to run the new rule parser.");
                return new HamlogRules(url, null, null);
            }, (delay, cancel) => { });
            Check(cache.CanRecompile(new AwardCatalog { Programs = new List<AwardProgram> { cached } }),
                "Old catalogs could not re-use their downloads.");
            cache.Scan(new[] { missing, cached }, default(System.Threading.CancellationToken), result => results.Add(result.Id));
            Check(results.SequenceEqual(new[] { cached.Id, missing.Id }), "The scan attempted new downloads before finishing the local cache.");
        });

        run("Reviewed contact rules choose the next tier and count distinct unrestricted QSOs", () => {
            const string own = "K1ABC";
            const string station = "VK5IPA";
            DateTime day = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
            var plan = new HamlogRulePlan { Kind = "contact-points", Required = 10, MemberPoints = 1, ExtraPoints = 1,
                Calls = new[] { station }, Contacts = new HamlogContactCriteria {
                    Repeat = HamlogContactCriteria.EveryQso, Levels = new[] { 5, 10 }
                }
            };
            plan.Validate();
            var award = new AwardProgram { Title = "Example tiers", Url = "https://hamlog.online/club/demo/2/", DownloadedRules = plan };
            award.Validate();
            var credit = HamlogRow(station, "24 Nov 2026", "20m", "FT8", "1");
            string totals = "<span class='award-dashboard-value'>2 / 10</span><span class='award-dashboard-value'>2 / 5</span>";
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, totals, credit, credit), award.Template, own, day);
            var history = new LogbookHistory("example.adi", new[] { 1, 2, 2, 3 }.Select(minute => new LogbookQso {
                Call = station, Station = own, Band = "20m", Mode = "FT8", StartedUtc = day.AddMinutes(minute)
            }).ToList());
            var score = new AwardProgress(award, history, own, call => null);
            Check(score.Points.Required == 5 && score.Points.PendingPoints == 1 && score.Needs(station, "20m", "FT8", day.AddMinutes(4)),
                "Confirmed repeats were double-counted or the next degree was incorrect.");
            score.Record(station, "20m", "FT8", day.AddMinutes(4));
            score.Record(station, "20m", "FT8", day.AddMinutes(4));
            score.Record(station, "20m", "FT8", day.AddMinutes(5));
            Check(score.Points.PendingPoints == 3 && !score.Points.ConfirmedComplete && score.Points.PendingEnough,
                "An identical log event or an unconfirmed contact changed the official total.");
            award.HasIssued = true;
            Check(!award.ClosedByIssuance, "Receiving the first degree excluded the other degrees.");
        });

        run("ARCK QSO series read HAMLOG tiers and group repeat digital contacts across the full date range", () => {
            const string own = "K1ABC", worked = "JA1AAA", newCall = "EA5ABC";
            DateTime day = new DateTime(2026, 11, 24, 13, 0, 0, DateTimeKind.Utc);
            var plan = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2000-01-01", Required = 100000, AlternativeRequired = 15000,
                MemberPoints = 1, ExtraPoints = 1, Contacts = new HamlogContactCriteria {
                    Repeat = HamlogContactCriteria.BandMode, GroupModes = true, AllStations = true, CreditBeforePublication = true,
                    AcceptObserverCredit = true
                }
            };
            plan.Validate();
            var award = new AwardProgram { Title = "ARCK QSO", Url = "https://hamlog.online/club/demo/24/",
                DownloadedRules = plan, PublishedFromUtc = "2022-01-15", HasIssued = true };
            award.Validate();
            const string totals = "<span class='award-dashboard-value'>3 / 15000</span><span class='award-dashboard-value'>3 / 25000</span>";
            string credit = HamlogRow(worked, "2 Jan 2000", "20m", "FT8", "1")
                + HamlogRow(@"EA5AAA\P", "2 Jan 2000", "20m", "SSB", "1")
                + HamlogRow("R3A-001/3", "2 Jan 2000", "20m", "CW", "1");
            HamlogStanding empty = HamlogStandingClient.Parse(HamlogPage(own, ""), award.Template, own, day);
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, totals, credit), award.Template, own, day);
            var history = new LogbookHistory("example.adi", new List<LogbookQso>());
            var score = new AwardProgress(award, history, own, call => null);
            Check(!award.ClosedByIssuance && score.Points.Required == 15000 && empty.ProvisionalRequired && empty.Required == 15000
                && score.Points.Standing.Points == 3 && award.Hamlog.Credits[1].Call == "EA5AAA/P" && award.Hamlog.Credits[2].Observation
                && !score.Needs(worked, "20m", "FT4", day) && score.Needs(worked, "40m", "FT4", day)
                && score.Needs(newCall, "20m", "FT8", day), "An eligible QSO was missed or issued degrees ended the whole series.");
            foreach (string invalid in new[] { totals.Replace("25000", "15000"), totals.Replace("25000", "100001"),
                totals.Replace("3 / 25000", "2 / 25000") })
            {
                bool rejected = false;
                try { HamlogStandingClient.Parse(HamlogPage(own, invalid, credit), award.Template, own, day); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "HAMLOG returned inconsistent or out-of-range degree totals.");
            }
        });

        run("South Kazakhstan awards match the first suffix letter and give precedence to published special calls", () => {
            const string url = "https://hamlog.online/club/arto/fe3a0fc7959eb1c6f3bf93e57f5d8eda/";
            var plan = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2026-03-01", UntilUtc = "2026-12-31",
                Required = 1600, MemberPoints = 100, ExtraPoints = 500, Calls = new[] { "UN7N" },
                BonusGroups = new[] { new HamlogBonusGroup { Points = 400, Targets = new[] { "UN2NC" } },
                    new HamlogBonusGroup { Points = 200, Area = true, Targets = new[] { "UN GQ" } },
                    new HamlogBonusGroup { Points = 100, Area = true, Targets = new[] { "UN NT" } } },
                Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode, GroupModes = true,
                    Adjustments = new[] { new HamlogPointAdjustment { Factor = 2, Bands = new[] { "160m", "2m" } } } }
            };
            plan.Validate();
            DateTime day = new DateTime(2026, 11, 24, 13, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "South Kazakhstan", Url = url, DownloadedRules = plan,
                Hamlog = new HamlogStanding { Callsign = "K1ABC", Required = 1600, CheckedUtc = day } };
            award.Validate();
            var history = new LogbookHistory("example.adi", new List<LogbookQso>());
            var progress = new HamlogPointProgress(award, history, "K1ABC", call => call.StartsWith("UN") ? "UN" : "K");
            Check(progress.CandidatePoints("UN8GAA", "160m", "FT8", day) == 400
                && progress.CandidatePoints("UN8QAA", "20m", "FT8", day) == 200
                && progress.CandidatePoints("UN8NAA", "2m", "FT4", day) == 200
                && progress.CandidatePoints("UN8TAA", "20m", "FT8", day) == 100
                && progress.CandidatePoints("UN7N", "2m", "FT8", day) == 1000
                && progress.CandidatePoints("UN2NC", "20m", "FT8", day) == 400
                && progress.CandidatePoints("UN8AG", "20m", "FT8", day) == 0
                && progress.CandidatePoints("K1GAA", "20m", "FT8", day) == 0
                && progress.CandidatePoints("UN8GAA", "20m", "FT8", day.AddMonths(2)) == 0,
                "The Kazakhstan regional or activity bonus was incorrect.");
        });

        run("Reviewed country rules apply an exact special-event window and group FT4 with FT8 where required", () => {
            const string own = "DL1ABC";
            DateTime day = new DateTime(2026, 7, 1, 13, 0, 0, DateTimeKind.Utc);
            var plan = new HamlogRulePlan { Kind = "contact-points", Required = 250, MemberPoints = 5, ExtraPoints = 10,
                Calls = new[] { "K2A" }, Contacts = new HamlogContactCriteria {
                    Repeat = HamlogContactCriteria.BandMode, GroupModes = true, Countries = new[] { "K" },
                    BonusFromUtc = "2026-07-01T13:00:00Z", BonusUntilUtc = "2026-07-08T04:00:00Z"
                }
            };
            plan.Validate();
            var item = new AwardProgram { Title = "Example country", Url = "https://hamlog.online/club/demo/3/", DownloadedRules = plan };
            item.Validate();
            item.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>5 / 250</span>",
                HamlogRow("K1ABC", "30 Jun 2026", "20m", "MFSK (FT4)", "5")), item.Template, own, day);
            var history = new LogbookHistory("example.adi", new List<LogbookQso>());
            var score = new AwardProgress(item, history, own, call => call.StartsWith("K", StringComparison.Ordinal) ? "K" : "DL");
            Check(score.Points.CandidatePoints("K2A", "20m", "FT8", day.AddSeconds(-1)) == 5
                && score.Points.CandidatePoints("K2A", "20m", "FT8", day) == 10
                && score.Points.CandidatePoints("K2A", "20m", "FT8", day.AddDays(8)) == 5
                && !score.Needs("K1ABC", "20m", "FT8", day) && score.Needs("K1ABC", "40m", "FT8", day)
                && !score.Needs("DL1AAA", "20m", "FT8", day),
                "The exact date, mode group, country or band was not respected.");
        });

        run("A credited SWL entry contributes points without becoming an outgoing station", () => {
            var plan = new HamlogRulePlan { Kind = "sky-hunter", Required = 100, MemberPoints = 10, ExtraPoints = 20,
                NoMultipliers = true, HfOnly = true, Calls = new[] { "U4MIR" }
            };
            plan.Validate();
            var item = new AwardProgram { Title = "Example sky", Url = "https://hamlog.online/club/nebo/1/", DownloadedRules = plan };
            item.Validate();
            DateTime day = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            item.Hamlog = HamlogStandingClient.Parse(HamlogPage("K1ABC", "<span class='award-dashboard-value'>10 / 100</span>",
                HamlogRow("R3A-014", "24 Nov 2026", "20m", "FT8", "10"),
                HamlogRow("W2AAA", "24 Nov 2026", "20m", "FT8", "-")), item.Template, "K1ABC", day);
            var score = new AwardProgress(item, new LogbookHistory("example.adi", new List<LogbookQso>()), "K1ABC", call => null);
            Check(item.Hamlog.Credits.Single().Observation && score.Points.NeedOnline == 90
                && score.Points.ConfirmedMissingFromLogbook == 0
                && score.Points.CandidatePoints("U4MIR", "160m", "FT8", day) == 20
                && !score.Needs("R3A-014", "20m", "FT8", day), "Observation affected search eligibility or flat points.");
        });

        run("Reviewed rules apply country-specific points and count a portable station once per UTC day", () => {
            const string own = "K1ABC";
            DateTime day = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
            var log = new LogbookHistory("example.adi", new List<LogbookQso>());
            var plan = new HamlogRulePlan { Kind = "contact-points", Required = 75, MemberPoints = 1, ExtraPoints = 1,
                BonusGroups = new[] {
                    new HamlogBonusGroup { Area = true, Points = 15, Targets = new[] { "DXCC I" } },
                    new HamlogBonusGroup { Area = true, Points = 10, Targets = new[] { "DXCC OE", "DXCC S5" } }
                },
                Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode, GroupModes = true,
                    Bands = new[] { "20m", "40m" } }
            };
            var award = new AwardProgram { Title = "Example weighted DXCC", Url = "https://hamlog.online/club/demo/4/",
                DownloadedRules = plan };
            award.Validate();
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>15 / 75</span>",
                HamlogRow("I1ABC", "24 Nov 2026", "20m", "FT8", "15")), award.Template, own, day);
            var score = new AwardProgress(award, log, own, call => call.StartsWith("S5") ? "S5" : call.StartsWith("I") ? "I" : "K");
            Check(!score.Needs("I1ABC", "20m", "FT4", day) && score.Points.CandidatePoints("I1ABC", "40m", "FT8", day) == 15
                && score.Points.CandidatePoints("S51ABC", "20m", "FT8", day) == 10
                && !score.Needs("I1XYZ", "2m", "FT8", day) && !score.Needs("K1XYZ", "20m", "FT8", day),
                "Country-specific points, band limits or duplicate checks were not applied.");

            plan = new HamlogRulePlan { Kind = "contact-points", Required = 25, MemberPoints = 1, ExtraPoints = 1,
                Calls = new[] { "OE6XMF" }, Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.Day,
                    PortableAliases = true, Modes = new[] { "DIGI" } } };
            award = new AwardProgram { Title = "Example station per day", Url = "https://hamlog.online/club/demo/5/",
                DownloadedRules = plan };
            award.Validate();
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>1 / 25</span>",
                HamlogRow("OE6XMF/P", "24 Nov 2026", "20m", "FT8", "1")), award.Template, own, day);
            score = new AwardProgress(award, log, own, call => "OE");
            Check(!score.Needs("OE6XMF", "40m", "FT4", day.AddHours(23))
                && score.Needs("OE6XMF/M", "40m", "FT8", day.AddDays(1))
                && !score.Needs("OE6XMF", "40m", "CW", day.AddDays(1)),
                "A portable variant, a prohibited mode or a new UTC day was counted incorrectly.");
        });

        run("District awards use HAMLOG's next tier and search by region", () => {
            const string own = "K1ABC";
            DateTime now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example RDA award", Url = "https://hamlog.online/club/demo/7/",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2026-11-01",
                    Required = 3, MemberPoints = 1, ExtraPoints = 1,
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.District, CountyPrefix = "VR",
                        LastCounty = 44, RetiredCounties = new[] { 8, 10 }, Levels = new[] { 2, 3 } } } };
            award.Validate();
            string totals = "<span class='award-dashboard-value'>1 / 2</span><span class='award-dashboard-value'>1 / 3</span>";
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, totals,
                HamlogRow("R3QAA", "23 Nov 2026", "20m", "FT8", "-", county: "VR-01", grid: "KO91")),
                award.Template, own, now);
            var history = new LogbookHistory("example.adi", new List<LogbookQso> {
                new LogbookQso { Call = "R3QBB", County = "VR-02", Locator = "KO92AA", StartedUtc = now.AddMonths(-1) },
                new LogbookQso { Call = "R3QCC", County = "VR-03", Locator = "KO93", StartedUtc = now.AddMonths(-1) },
                new LogbookQso { Call = "R3QCC", County = "VG-03", Locator = "KO93", StartedUtc = now.AddMonths(-1) },
                new LogbookQso { Call = "R3QDD", County = "VR-08", Locator = "KO94", StartedUtc = now.AddMonths(-1) },
                new LogbookQso { Call = "R3QEE/AM", County = "VR-04", Locator = "KO95", StartedUtc = now.AddMonths(-1) },
            });
            var progress = new AwardProgress(award, history, own, call => "UA");
            Check(progress.Online.Required == 2 && progress.Online.Confirmed == 1
                && progress.Needs("R3QBB", "40m", "FT8", now)
                && progress.Needs("R3QBB", "40m", "FT8", now, "KO93")
                && !progress.Needs("R3QAA", "20m", "FT8", now, "KO91")
                && progress.Needs("R3QCC", "40m", "FT8", now, "KO93")
                && progress.Needs("R3QDD", "40m", "FT8", now, "KO94")
                && !progress.Needs("R3QEE/AM", "40m", "FT8", now, "KO95")
                && progress.Needs("R3QFF", "40m", "FT8", now, "KO92")
                && !progress.Needs("R4CAA", "40m", "FT8", now, "KO92"),
                "The search used a district or grid boundary instead of the area and repeat history.");
            progress.Record(new LogbookQso { Call = "R3QBB", Band = "40m", Mode = "FT8", Locator = "KO92", StartedUtc = now });
            Check(progress.Available("40m", "FT8", now) && !progress.Needs("R3QBB", "40m", "FT8", now)
                && progress.Online.Confirmed == 1 && !progress.Complete,
                "A local QSO was counted by HAMLOG or did not reserve a repeat.");
        });

        run("Regional monitoring waits for HAMLOG to meet the secondary district threshold", () => {
            const string own = "K1ABC";
            DateTime now = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example city award", Url = "https://hamlog.online/club/demo/77/", Selected = true,
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", Required = 2, MemberPoints = 1, ExtraPoints = 1,
                    FromUtc = "2026-01-01", Contacts = new HamlogContactCriteria {
                        Repeat = HamlogContactCriteria.BandMode, GroupModes = true, CountyPrefix = "TA", LastCounty = 7,
                        Additional = new[] { new HamlogContactRequirement {
                            Kind = HamlogContactRequirement.Counties, Required = 2, ReportedByHamlog = true } }
                    }
                }
            };
            award.Validate();
            string primary = "<span class='award-dashboard-value'>2 / 2</span>";
            string secondary = "<li>Required counties: <span class='award-dashboard-value'>1 / 2</span></li>";
            string first = HamlogRow("R4PAA", "22 Nov 2026", "80m", "FT8", "1", true, "TA-01", "LO44");
            string second = HamlogRow("R4PAB", "23 Nov 2026", "80m", "FT8", "1", true, "TA-01", "LO44");
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, primary + secondary, first, second), award.Template, own, now);
            foreach (string invalid in new[] { "", secondary.Replace("1 / 2", "2 / 2") })
            {
                bool rejected = false;
                try { HamlogStandingClient.Parse(HamlogPage(own, primary + invalid, first, second), award.Template, own, now); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "An absent or incorrect required district count was accepted.");
            }
            var old = new LogbookQso { Call = "R4PBA", Station = own, Band = "80m", Mode = "FT8",
                Locator = "LO45", County = "TA-02", StartedUtc = now.AddYears(-1) };
            var search = new AwardSearch(new AwardCatalog { Programs = new List<AwardProgram> { award } },
                new LogbookHistory("example.adi", new List<LogbookQso> { old }), own, "40m", "FT8", call => "UA");
            Check(search.Available(now) == 1 && search.Score("R4PBA", now) == 1
                && search.Score("R4PBA", now, "LO46") == 1 && search.Score("R4PAA", now, "LO44") == 1,
                "Regional search depended on the locator or excluded a new band.");
            search.Record(new LogbookQso { Call = "R4PBA", Station = own, Band = "40m", Mode = "FT8",
                Locator = "LO45", StartedUtc = now });
            Check(search.Score("R4PBA", now) == 0 && search.Available(now) == 1 && award.Hamlog.Points == 2 && award.Hamlog.Credits.Count == 2,
                "Logging the station did not reserve a repeat, or a local contact changed the HAMLOG result.");
        });

        run("Mandatory district contacts and separate score multipliers survive the preferences round trip", () => {
            const string own = "K1ABC";
            DateTime boundary = new DateTime(2026, 11, 23, 21, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example activity", Url = "https://hamlog.online/club/demo/78/",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2026-11-20",
                    Required = 30, MemberPoints = 1, ExtraPoints = 20, Calls = new[] { "R4CAA" },
                    BonusGroups = new[] { new HamlogBonusGroup { Area = true, Points = 10, Targets = new[] { "SA-45" } } },
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode,
                        Additional = new[] { new HamlogContactRequirement { Kind = HamlogContactRequirement.CountyContacts,
                            CountyCodes = new[] { "SA-45" }, Required = 2, ReportedByHamlog = true } },
                        Adjustments = new[] {
                            new HamlogPointAdjustment { Factor = 2, Bands = new[] { "160m" } },
                            new HamlogPointAdjustment { Factor = 2, FromUtc = "2026-11-23T21:00:00Z", UntilUtc = "2026-11-24T21:00:00Z" }
                        }
                    }
                }
            };
            award.Validate();
            var settings = new AppPreferences();
            settings.Awards.Programs.Add(award);
            var store = new AppPreferencesStore(TestWorkspace.Create());
            store.Save(settings);
            award = store.Load().Awards.Programs.Single();
            string totals = "<span class='award-dashboard-value'>20 / 30</span>"
                + "<li>Required station contacts: <span class='award-dashboard-value'>1 / 2</span></li>";
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, totals,
                HamlogRow("R4CBB", "23 Nov 2026", "160m", "FT8", "20", true, "SA-45", "LO42")), award.Template, own, boundary);
            var score = new HamlogPointProgress(award, new LogbookHistory("example.adi", new List<LogbookQso>()), own, call => "UA");
            Check(score.CandidatePoints("R4CBB", "40m", "FT8", boundary.AddSeconds(-1), "LO42") == 10
                && score.CandidatePoints("R4CBB", "40m", "FT8", boundary, "LO42") == 20
                && score.CandidatePoints("R4CBB", "40m", "FT8", boundary.AddDays(1), "LO42") == 10
                && score.CandidatePoints("R4CAA", "160m", "FT8", boundary) == 80,
                "The listed UTC window, exact district and band multiplier were not combined correctly.");
            score.Record("R4CAA", "160m", "FT8", boundary);
            Check(!score.PendingEnough && score.CandidatePoints("R4CAA", "40m", "FT8", boundary) == 0
                && score.CandidatePoints("R4CBB", "40m", "FT8", boundary, "LO42") == 20,
                "Additional points alone satisfied a required district contact.");
            score.Record("R4CBB", "40m", "FT8", boundary, locator: "LO42");
            Check(score.PendingEnough && !score.ConfirmedComplete,
                "A second qualifying QSO in the same district did not satisfy the contact quota pending confirmation.");
        });

        run("A recurring scoring window and a same-day alternative follow the calendar each year", () => {
            const string own = "K1ABC";
            DateTime birthday = new DateTime(2026, 10, 3, 6, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example annual award", Url = "https://hamlog.online/club/demo/83/",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", Required = 30, MemberPoints = 1, ExtraPoints = 1,
                    FromUtc = "2025-10-01", BonusGroups = new[] { new HamlogBonusGroup { Area = true, Points = 1, Targets = new[] { "RA" } } },
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.EveryQso, ShortcutDay = "10-03", ShortcutContacts = 3,
                        Adjustments = new[] { new HamlogPointAdjustment { Factor = 3, AnnualFrom = "10-01", AnnualUntil = "10-07" } }
                    }
                }
            };
            award.Validate();
            var store = new AppPreferencesStore(TestWorkspace.Create());
            var preferences = new AppPreferences();
            preferences.Awards.Programs.Add(award);
            store.Save(preferences);
            award = store.Load().Awards.Programs.Single();
            string priorYear = HamlogRow("R3SAA", "3 Oct 2025", "40m", "FT8", "3");
            string currentYear = HamlogRow("R3SAB", "3 Oct 2026", "40m", "FT8", "3");
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>6 / 30</span>",
                priorYear, currentYear), award.Template, own, birthday);
            var history = new LogbookHistory("example.adi", new List<LogbookQso> {
                new LogbookQso { Call = "R3SAB", Station = own, Band = "40m", Mode = "FT8", StartedUtc = birthday.AddHours(-1) }
            });
            var score = new HamlogPointProgress(award, history, own, call => "UA");
            Check(score.Pending.Count == 0 && score.CandidatePoints("R3SAC", "40m", "FT8", birthday.AddDays(-3)) == 1
                && score.CandidatePoints("R3SAC", "40m", "FT8", birthday.AddYears(1)) == 3
                && score.CandidatePoints("R3SAC", "40m", "FT8", birthday.AddDays(4)) == 3
                && score.CandidatePoints("R3SAC", "40m", "FT8", birthday.AddDays(5)) == 1
                && score.CandidatePoints("R3PAA", "40m", "FT8", birthday) == 0,
                "The recurring dates, regional eligibility or existing QSO reconciliation were lost.");
            score.Record("R3SAC", "40m", "FT8", birthday);
            score.Record("R3SAC", "40m", "FT8", birthday);
            Check(!score.PendingEnough && !score.ConfirmedComplete, "A previous year or a duplicate completed the special date.");
            score.Record("R3SAD", "40m", "FT8", birthday.AddMinutes(1));
            Check(score.PendingEnough && !score.ConfirmedComplete && score.PendingPoints == 6 && !score.CanSearch,
                "Three QSOs on the same date did not suspend calls pending confirmation.");
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>12 / 30</span>",
                priorYear, currentYear, HamlogRow("R3SAC", "3 Oct 2026", "40m", "FT8", "3"),
                HamlogRow("R3SAD", "3 Oct 2026", "40m", "FT8", "3")), award.Template, own, birthday);
            var confirmed = new HamlogPointProgress(award, history, own, call => "UA");
            Check(confirmed.ConfirmedShortcut && confirmed.ConfirmedComplete && confirmed.NeedOnline == 18,
                "Confirmed special-date completion was incorrectly tied to the ordinary points target.");
        });

        run("Regional portable points only apply to the eligible suffix and configured bands", () => {
            const string own = "K1ABC";
            DateTime today = new DateTime(2026, 11, 24, 6, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example portable scores", Url = "https://hamlog.online/club/demo/84/",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", Required = 44, MemberPoints = 1, ExtraPoints = 5,
                    Calls = new[] { "RK3NWA" }, BonusGroups = new[] { new HamlogBonusGroup { Area = true, Points = 1, Targets = new[] { "KS" } } },
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode, PortableRegionalCalls = true,
                        Bands = new[] { "20m", "40m" },
                        Adjustments = new[] { new HamlogPointAdjustment { Factor = 2, PortableSuffixes = new[] { "/P" } } } }
                }
            };
            award.Validate();
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>2 / 44</span>",
                HamlogRow("R3NAA/P", "23 Nov 2026", "40m", "FT8", "2")), award.Template, own, today);
            var score = new HamlogPointProgress(award, new LogbookHistory("example.adi", new List<LogbookQso>()), own, call => "UA");
            Check(score.CandidatePoints("R3NAA/P", "40m", "FT8", today) == 0 && score.CandidatePoints("R3NAA/P", "20m", "FT8", today) == 2
                && score.CandidatePoints("R3NBB", "40m", "FT8", today) == 1 && score.CandidatePoints("RK3NWA", "40m", "FT8", today) == 5
                && score.CandidatePoints("R3NBB/M", "20m", "FT8", today) == 0
                && score.CandidatePoints("R3NBB/4/P", "20m", "FT8", today) == 0
                && score.CandidatePoints("R3SAA/P", "20m", "FT8", today) == 0 && !score.Available("2m", "FT8", today),
                "Portable scoring selected the wrong region, suffix, band or duplicate.");
        });

        run("Calendar-day repeats use the event's local day while official dates remain conservative", () => {
            const string own = "K1ABC";
            DateTime boundary = new DateTime(2021, 10, 18, 21, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example Moscow-time event", Url = "https://hamlog.online/club/demo/85/",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", Required = 10, MemberPoints = 1, ExtraPoints = 1,
                    UntilUtc = "2021-10-19T21:00:00Z", Calls = new[] { "R4AAA", "R4ABB" },
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.DayBandMode, RepeatDayOffsetMinutes = 180,
                        Adjustments = new[] { new HamlogPointAdjustment { Factor = 2, FromUtc = "2021-10-18T21:00:00Z", UntilUtc = "2021-10-19T21:00:00Z" } }
                    }
                }
            };
            award.Validate();
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>1 / 10</span>",
                HamlogRow("R4ABB", "18 Oct 2021", "40m", "FT8", "1")), award.Template, own, boundary);
            var score = new HamlogPointProgress(award, new LogbookHistory("example.adi", new List<LogbookQso>()), own);
            score.Record("R4AAA", "40m", "FT8", boundary.AddMinutes(-1));
            Check(score.CandidatePoints("R4AAA", "40m", "FT8", boundary.AddSeconds(-1)) == 0
                && score.CandidatePoints("R4AAA", "40m", "FT8", boundary) == 2
                && score.CandidatePoints("R4ABB", "40m", "FT8", boundary) == 0
                && score.CandidatePoints("R4ABB", "20m", "FT8", boundary) == 2
                && !score.Available("40m", "FT8", boundary.AddDays(1)), "Local days and publication dates were treated as UTC days.");
        });

        run("A regional search covers the region while HAMLOG credit keeps the published district subset", () => {
            const string own = "K1ABC";
            DateTime now = new DateTime(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example river bank", Url = "https://hamlog.online/club/demo/80/",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", Required = 2, MemberPoints = 1, ExtraPoints = 1,
                    FromUtc = "2026-01-01", Contacts = new HamlogContactCriteria {
                        Repeat = HamlogContactCriteria.District, CountyPrefix = "SA", LastCounty = 58,
                        EligibleCounties = new[] { 19, 23, 45 }, ActivatorAreas = new[] { "SA" }
                    }
                }
            };
            award.Validate();
            string total = "<span class='award-dashboard-value'>1 / 2</span>";
            var known = HamlogRow("R4CAA", "23 Nov 2026", "40m", "FT8", "-", true, "SA-19", "LO42");
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, total, known), award.Template, own, now);
            bool rejected = false;
            try { HamlogStandingClient.Parse(HamlogPage(own, total, known.Replace("SA-19", "SA-20")), award.Template, own, now); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "HAMLOG credit outside the published district subset was accepted.");
            var history = new LogbookHistory("example.adi", new List<LogbookQso> {
                new LogbookQso { Call = "R4CAB", Station = own, County = "SA-23", Locator = "LO43", StartedUtc = now.AddYears(-1) },
                new LogbookQso { Call = "R4CAC", Station = own, County = "SA-20", Locator = "LO43", StartedUtc = now.AddYears(-1) }
            });
            var progress = new AwardProgress(award, history, own, call => call == own ? "K" : "UA");
            Check(progress.Needs("R4CAB", "80m", "FT8", now)
                && progress.Needs("R4CAC", "80m", "FT8", now)
                && !progress.Needs("R4PAA", "80m", "FT8", now)
                && !progress.Needs("R4CAA", "40m", "FT4", now), "The search used a district boundary or forgot an already credited contact.");
            Check(!new AwardProgress(award, history, "R4CAA", call => "UA").CanSelectForSearch(now),
                "A regional activator could start the hunter rule.");
        });

        run("RDA multiplier reproduces official totals and values a genuinely new district", () => {
            const string own = "K1ABC";
            DateTime now = new DateTime(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example district score", Url = "https://hamlog.online/club/demo/81/",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", Required = 36, MemberPoints = 2, ExtraPoints = 1,
                    FromUtc = "2026-01-01", Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode,
                        MultiplyByCountyCount = true, CountyPrefix = "TL", LastCounty = 38 },
                    BonusGroups = new[] { new HamlogBonusGroup { Area = true, Points = 2, Targets = new[] { "TL" } } }
                }
            };
            award.Validate();
            string total = "<span class='award-dashboard-value'>12 / 36</span>";
            string first = HamlogRow("R3PAA", "21 Nov 2026", "40m", "FT8", "2", true, "TL-01", "KO92");
            string second = HamlogRow("R3PAB", "22 Nov 2026", "40m", "FT8", "2", true, "TL-02", "KO93");
            string third = HamlogRow("R3PAC", "23 Nov 2026", "40m", "FT8", "2", true, "", "KO92");
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, total, first, second, third), award.Template, own, now);
            bool rejected = false;
            try { HamlogStandingClient.Parse(HamlogPage(own, total.Replace("12", "6"), first, second, third), award.Template, own, now); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "HAMLOG's raw points were accepted without the multiplier.");
            var history = new LogbookHistory("example.adi", new List<LogbookQso> {
                new LogbookQso { Call = "R3PBA", Station = own, County = "TL-03", Locator = "KO94", StartedUtc = now.AddYears(-1) },
                new LogbookQso { Call = "R3PBB", Station = own, County = "RA-04", Locator = "KO95", StartedUtc = now.AddYears(-1) }
            });
            var score = new HamlogPointProgress(award, history, own, call => call == own ? "K" : "UA");
            Check(score.ConfirmedCountyMultiplier == 2 && score.CandidatePoints("R3PBA", "80m", "FT8", now, "KO94") == 12
                && score.CandidatePoints("R3PBA", "80m", "FT8", now, "KO92") == 4
                && score.CandidatePoints("R3PBB", "80m", "FT8", now, "KO95") == 0,
                "A known district, a regular regional call, and a conflicting district were scored alike.");
            score.Record("R3PBA", "80m", "FT8", now, locator: "KO94");
            Check(score.PendingPoints == 12 && award.Hamlog.Points == 12
                && score.CandidatePoints("R3PAB", "80m", "FT8", now, "KO93") == 6,
                "Projection did not reserve the new district or changed official credit.");
        });

        run("A published square award excludes worked grids and respects the callsign-band-mode repeat rule", () => {
            const string own = "K1ABC";
            DateTime now = new DateTime(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example squares", Url = "https://hamlog.online/club/demo/82/",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", Required = 4, MemberPoints = 1, ExtraPoints = 1,
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.Locator,
                        Countries = new[] { "HC", "HC8" }, Levels = new[] { 2, 4 } }
                }
            };
            award.Validate();
            string total = "<span class='award-dashboard-value'>1 / 2</span><span class='award-dashboard-value'>1 / 4</span>";
            var row = HamlogRow("HC1AAA", "23 Nov 2026", "40m", "FT8", "1", true, "", "FI07JA");
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, total, row), award.Template, own, now);
            var progress = new AwardProgress(award, LogbookHistory.Empty, own,
                call => call.StartsWith("HC", StringComparison.Ordinal) ? "HC" : "K");
            Check(progress.Online.Required == 2 && award.Hamlog.ConfirmedSquares.SequenceEqual(new[] { "FI07" })
                && !progress.Needs("HC1AAA", "40m", "FT8", now, "FI08")
                && progress.Needs("HC1AAA", "80m", "FT8", now, "FI08")
                && !progress.Needs("HC2AAA", "40m", "FT8", now, "FI07")
                && !progress.Needs("HC2AAA", "40m", "FT8", now, "RR73")
                && !progress.Needs("K2AAA", "40m", "FT8", now, "FI08"),
                "Selection ignored the tier, the fresh grid, the entity, or the repeat rule.");
            progress.Online.Record(new LogbookQso { Call = "HC2AAA", Station = own, Band = "40m",
                Mode = "FT8", Locator = "FI08", StartedUtc = now });
            Check(!progress.Available("40m", "FT8", now) && !progress.Complete,
                "Local square credit was treated as a confirmed award or the search continued after reservation.");
        });

        run("Required club members are distinct and contacts may predate the catalog listing", () => {
            const string own = "K1ABC";
            DateTime now = new DateTime(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example club diploma", Url = "https://hamlog.online/club/demo/83/",
                PublishedFromUtc = "2025-09-27", DownloadedRules = new HamlogRulePlan { Kind = "contact-points",
                    Required = 2, MemberPoints = 1, ExtraPoints = 1,
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode,
                        CreditBeforePublication = true, Roster = "firac", PortableAliases = true, Modes = new[] { "DIGI" },
                        Additional = new[] { new HamlogContactRequirement { Kind = HamlogContactRequirement.Stations,
                            Required = 2, ReportedByHamlog = true, DistinctStations = true, PortableStations = true,
                            Calls = new[] { "OE5WRO", "OE5ADL" } } } }
                }
            };
            award.Validate();
            string total = "<span class='award-dashboard-value'>2 / 2</span>"
                + "<li>Required station contacts: <span class='award-dashboard-value'>2 / 2</span></li>";
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, total,
                HamlogRow("OE5WRO", "23 Nov 2026", "40m", "FT8", "1"),
                HamlogRow("OE5WRO/P", "23 Nov 2026", "80m", "FT8", "1")), award.Template, own, now);
            var regular = new HamlogPointProgress(award, new LogbookHistory("example.adi", new List<LogbookQso>()), own);
            Check(regular.Additional.Single().Confirmed == 1 && !regular.ConfirmedComplete
                && regular.CandidatePoints("OE5WRO", "20m", "FT8", now) == 0
                && regular.CandidatePoints("OE5ADL/P", "20m", "FT8", now) == 1,
                "Multiple contacts with one member satisfied the distinct station requirement.");
            var history = new LogbookHistory("example.adi", new List<LogbookQso> {
                new LogbookQso { Call = "OE5ADL", Station = own, Band = "20m", Mode = "FT8", StartedUtc = now.AddYears(-3) }
            });
            var earlier = new HamlogPointProgress(award, history, own);
            Check(earlier.PendingEnough && !earlier.ConfirmedComplete, "A valid contact before the publishing date was ignored.");
        });

        run("RCPW days of activity can include an exact temporary call and a separate applicant factor", () => {
            const string own = "R0AAA";
            DateTime boundary = new DateTime(2023, 5, 29, 0, 0, 0, DateTimeKind.Utc);
            var award = new AwardProgram { Title = "Example RCPW day", Url = "https://hamlog.online/club/rcpw/84/",
                DownloadedRules = new HamlogRulePlan { Kind = "rcpw-hunter", FromUtc = "2023-05-20", BonusUntilUtc = "2023-05-29",
                    MemberPoints = 3, ExtraPoints = 5, RcpwOnlineAfterPoints = 5, Required = 105, AlternativeRequired = 105,
                    Calls = new[] { "R105PW" }, RcpwSpecialPoints = 5, RcpwDoubleDistrictZero = true }
            };
            award.Validate();
            var live = HamlogPage(own, "<span class='award-dashboard-value'>20 / 105</span>",
                HamlogRow("R105PW", "28 May 2023", "20m", "FT8", "10"), HamlogRow("R3QAA", "29 May 2023", "20m", "FT8", "10"));
            award.Hamlog = HamlogStandingClient.Parse(live, award.Template, own, boundary);
            award.Hamlog.RcpwMembers = new[] { "R3QAA" };
            var score = new HamlogPointProgress(award, new LogbookHistory("example.adi", new List<LogbookQso>()), own);
            Check(award.Hamlog.RcpwAfterPoints == 5 && score.CandidatePoints("R105PW", "40m", "FT8", boundary.AddSeconds(-1)) == 10
                && score.CandidatePoints("R105PW", "40m", "FT8", boundary) == 0
                && score.CandidatePoints("R3QAA", "40m", "FT8", boundary) == 10 && award.DownloadedRules.RcpwFactor("K0AAA") == 1,
                "The temporary call remained eligible after the event or the applicant bonus was applied incorrectly.");
        });

        run("RCPW preserves published rates, adopts a corroborated online rate and groups a member's calls", () => {
            const string own = "K1ABC";
            DateTime boundary = new DateTime(2024, 2, 3, 0, 0, 0, DateTimeKind.Utc);
            var plan = new HamlogRulePlan { Kind = "rcpw-hunter", Required = 100, AlternativeRequired = 100,
                FromUtc = "2024-01-26", BonusUntilUtc = "2024-02-03", MemberPoints = 3, ExtraPoints = 5, RcpwOnlineAfterPoints = 5 };
            var award = new AwardProgram { Title = "Example club points", Url = "https://hamlog.online/club/demo/8/", DownloadedRules = plan };
            award.Validate();
            var members = HamlogRcpwRoster.ParseMembers("<table><tr><td>#12</td><td>R3QAA, R3QAB</td><td>Member</td></tr>"
                + "<tr><td>#13</td><td>R3QBB</td><td>Member</td></tr></table>");
            string totals = "<span class='award-dashboard-value'>5 / 100</span>";
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, totals, HamlogRow("R3QAA", "3 Feb 2024", "20m", "FT8", "5")),
                award.Template, own, boundary);
            award.Hamlog.RcpwMembers = members.Keys.ToArray();
            var log = new LogbookHistory("example.adi", new List<LogbookQso>());
            var score = new AwardProgress(award, log, own, call => "UA");
            Check(award.Hamlog.RcpwAfterPoints == 5 && score.Points.CandidatePoints("R3QBB", "40m", "FT8", boundary) == 5,
                "The corroborated HAMLOG rate was not used for pending contacts.");
            var empty = HamlogStandingClient.Parse(HamlogPage(own, ""), award.Template, own, boundary);
            Check(empty.RcpwAfterPoints == 3, "The published rate was not used for an empty standing.");
            award.DownloadedRules = new HamlogRulePlan { Kind = "contact-points", Required = 25, MemberPoints = 1, ExtraPoints = 1,
                Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.Call, Roster = "rcpw" } };
            award.Validate();
            award.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>1 / 25</span>",
                HamlogRow("R3QAA", "3 Feb 2024", "20m", "FT8", "1")), award.Template, own, boundary);
            var membership = members.ToArray();
            award.Hamlog.RcpwMemberNumbers = membership.Select(member => member.Value).ToArray();
            award.Hamlog.RcpwMembers = membership.Select(member => member.Key).ToArray();
            score = new AwardProgress(award, log, own, call => "UA");
            Check(!score.Needs("R3QAB", "40m", "FT8", boundary) && score.Needs("R3QBB", "40m", "FT8", boundary),
                "A previously worked member's alternate callsign was selected again.");
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
            var withoutResult = new AwardSearch(unverified, LogbookHistory.Empty, "K1AAA", "80m", "FT8", dxcc);
            Check(withoutResult.Score("R3PAA", at) == 1 && withoutResult.Score("R3AAA", at) == 0,
                "A missing credit lookup blocked regional search or exposed an old broad country rule.");
        });

        run("Nationwide RDA and single-region awards choose stations by callsign and use HAMLOG totals", () => {
            const string own = "K1ABC";
            DateTime at = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
            var national = new AwardProgram { Title = "RDA", Url = "https://hamlog.online/club/demo/96/", Selected = true,
                DownloadedRules = new HamlogRulePlan { Kind = "rda-search", Required = 20 } };
            var tula = HamlogAward.Gunsmiths.Create();
            tula.Selected = true;
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { national, tula } };
            foreach (AwardProgram program in catalog.Programs) program.Validate();
            Func<string, string> dxcc = call => call.StartsWith("R0") ? "UA9" : call.StartsWith("UA2") ? "UA2"
                : call.StartsWith("R3") ? "UA" : call.StartsWith("RI1") ? "R1FJ" : "DL";
            var search = new AwardSearch(catalog, LogbookHistory.Empty, own, "40m", "FT8", dxcc);
            Check(search.Score("R3PAA", at) == 2 && search.Score("R3PAA/P", at) == 2
                && search.Score("R3SAA", at) == 1 && search.Score("R0SAA", at) == 1 && search.Score("UA2FAA", at) == 1
                && search.Score("RI1FJ", at) == 0 && search.Score("DL1AAA", at, "KO85") == 0, "The nationwide or Tula search used a wrong territory.");
            search.Record(new LogbookQso { Call = "R3PAA", Band = "40m", Mode = "FT8", Station = own, StartedUtc = at });
            Check(search.Score("R3PAA", at) == 0 && !search.Reason("R3PBB", at).Contains("очк."),
                "A local contact was repeated or presented as confirmed credit.");
            national.Hamlog = HamlogStandingClient.Parse(HamlogPage(own, "<span class='award-dashboard-value'>20 / 20</span>",
                HamlogRow("R3A-123-SWL", "24 Nov 2026", "40M", "FT8", "+20")), national.Template, own, at);
            Check(new AwardSearch(catalog, LogbookHistory.Empty, own, "40m", "FT8", dxcc).Score("UA2FAA", at) == 0,
                "HAMLOG's latest complete standing did not close the nationwide search.");
        });

        run("Bryansk credit distinguishes digital contacts from older CW rows", () => {
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
