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
                + "\"programs\":[{\"title\":\"Local name\",\"url\":\"https://hamlog.online/neca/info.php\",\"hasIssued\":true,"
                + "\"goals\":[{\"kind\":\"call\",\"value\":\"W8ABC\"}]}]}");
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
                && catalog.Programs.Any(row => row.HasSearchRules && row.Template == HamlogAward.Russia),
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

        run("Catalog refresh and re-reading conditions preserve an edited rule and keep the new original", () => {
            string url = "https://hamlog.online/club/nebo/29/";
            Func<string, HamlogRulePlan> plan = call => new HamlogRulePlan {
                Kind = "sky-hunter", Required = 95, MemberPoints = 10, ExtraPoints = 20, Calls = new[] { call }
            };
            var saved = new AppPreferences();
            var award = new AwardProgram { Title = "Old", Url = url, PublicEntry = true,
                DownloadedRules = plan("W2ABC"), EditedRules = plan("W2ABD") };
            award.Validate();
            saved.Awards.Programs.Add(award);
            var newEntry = new AwardProgram { Title = "Updated", Url = url, PublicEntry = true };
            newEntry.Validate();
            DateTime now = DateTime.UtcNow;
            saved.Awards.ApplyPublicCatalog(new HamlogCatalogSnapshot(new[] { newEntry }), now);
            AwardProgram refreshed = saved.Awards.Programs.Single(item => item.Url == url);
            refreshed.ApplyDocument(new HamlogRuleResult { Status = "downloaded", CheckedUtc = now, Plan = plan("W2ABE") });
            var storage = new AppPreferencesStore(TestWorkspace.Create());
            storage.Save(saved);
            AwardProgram loaded = storage.Load().Awards.Programs.Single(item => item.Url == url);
            Check(loaded.Title == "Updated" && loaded.HasEditedRules && loaded.EffectiveRules.Calls.Single() == "W2ABD"
                && loaded.DownloadedRules.Calls.Single() == "W2ABE", "A catalog refresh or restart replaced a manual edit.");
            using (var editor = new HamlogPlanDialog(loaded))
            {
                const System.Reflection.BindingFlags access = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var calls = (TextBox)typeof(HamlogPlanDialog).GetField("calls", access).GetValue(editor);
                Check(calls.Text == "W2ABD", "The editor did not display the active manual values.");
                calls.Text = "W2ABF";
                typeof(HamlogPlanDialog).GetMethod("Save", access).Invoke(editor, null);
                Check(editor.Edited.Calls.Single() == "W2ABF" && loaded.EditedRules.Calls.Single() == "W2ABD", "The editor did not return an independent edit.");
                typeof(HamlogPlanDialog).GetMethod("LoadValues", access).Invoke(editor, new object[] { loaded.DownloadedRules, false });
                Check(calls.Text == "W2ABE", "Restore did not display the original values.");
                typeof(HamlogPlanDialog).GetMethod("Save", access).Invoke(editor, null);
                loaded.EditedRules = editor.Edited;
            }
            Check(loaded.EffectiveRules.Calls.Single() == "W2ABE" && !loaded.HasEditedRules, "Reverting did not restore the downloaded rules.");
            saved.Awards.Programs.Single(item => item.Url == url).EditedRules = plan("W2ABD");
            using (var dialog = new AwardsDialog(saved.Awards, "K1ABC"))
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

        run("HAMLOG filters and checkboxes use rule availability, issuance and dates", () => {
            DateTime now = DateTime.UtcNow;
            var ready = new AwardProgram { Id = "ready", Title = "Available award", Sponsor = "Example Club",
                Section = "Клубные и региональные", Goals = new List<AwardGoal> { new AwardGoal { Kind = "call", Value = "W2ABC" } } };
            AwardProgram issued = ready.Copy(), expired = ready.Copy(), upcoming = ready.Copy();
            issued.Id = "issued"; issued.Title = "Issued award"; issued.HasIssued = true;
            expired.Id = "expired"; expired.Title = "Expired award"; expired.UntilUtc = now.Date.AddDays(-2).ToString("yyyy-MM-dd");
            upcoming.Id = "upcoming"; upcoming.Title = "Upcoming award"; upcoming.FromUtc = now.Date.AddDays(2).ToString("yyyy-MM-dd");
            var unknown = new AwardProgram { Id = "unknown", Title = "Award without rules", Sponsor = ready.Sponsor,
                Section = ready.Section, PublicEntry = true, Url = "https://hamlog.online/club/demo/12/" };
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { ready, issued, expired, upcoming, unknown } };
            foreach (AwardProgram program in catalog.Programs) program.Validate();
            issued.Selected = expired.Selected = upcoming.Selected = true;
            using (var dialog = new AwardsDialog(catalog, "K1ABC"))
            {
                var grid = dialog.Controls.OfType<DataGridView>().Single();
                var state = dialog.Controls.OfType<ComboBox>().Single(box => box.AccessibleName == "Состояние");
                var section = dialog.Controls.OfType<ComboBox>().Single(box => box.AccessibleName == "Раздел");
                var query = dialog.Controls.OfType<TextBox>().Single(box => box.AccessibleName == "Название или клуб");
                Check(dialog.Controls.OfType<Button>().Count() == 3 && grid.Columns.Cast<DataGridViewColumn>()
                    .Select(column => column.HeaderText).SequenceEqual(new[] { "Поиск", "Диплом", "Клуб или организатор", "Состояние" }),
                    "The award list exposed controls outside the search and receipt workflow.");
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
                state.SelectedItem = "Полученные";
                Check(visible().SequenceEqual(new[] { "issued" }), "The received filter lost an issuance record.");
                state.SelectedItem = "Срок истёк";
                Check(visible().SequenceEqual(new[] { "expired" }), "The expired filter returned the wrong awards.");
                state.SelectedItem = "Все дипломы";
                foreach (string inactive in new[] { "issued", "expired", "upcoming", "unknown" })
                {
                    DataGridViewCell cell = checkCell(inactive);
                    Check(cell.ReadOnly && !(cell is DataGridViewCheckBoxCell) && Object.Equals(cell.Value, "—"),
                        "An inactive award has an editable selection field.");
                }
                Check(dialog.Catalog.Programs.Where(program => program.Selected).Select(program => program.Id).SequenceEqual(new[] { "ready" })
                    && Object.Equals(checkCell("ready").Value, true), "Changing filters lost an eligible choice or kept a stale choice.");
            }
        });

        run("Award monitoring respects an exact deadline and disables received awards of any type", () => {
            DateTime end = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            AwardProgram limited = HamlogAward.Gunsmiths.Create(), issued = HamlogAward.Russia.Create();
            limited.PublishedUntilUtc = "2026-11-24T12:00:00Z";
            issued.HasIssued = true;
            limited.Selected = issued.Selected = true;
            var search = new AwardSearch(new AwardCatalog { Programs = new List<AwardProgram> { limited, issued } },
                "K1ABC", "80m", "FT8", call => "UA");
            Check(search.MatchCount("R3PAA", end.AddSeconds(-1), "KO92") == 1
                && search.MatchCount("R3SAA", end.AddSeconds(-1), "KO92") == 0,
                "An issued award contributed a target, or the deadline closed the remaining award early.");
            Check(search.Available(end) == 0 && search.MatchCount("R3PAA", end) == 0,
                "A cached selection still contributed targets after the exact expiration time.");
        });

        run("Previous preferences reopen with original search rules and ignore obsolete award credit", () => {
            string directory = TestWorkspace.Create();
            var store = new AppPreferencesStore(directory);
            var award = HamlogAward.Murom.Create();
            award.Selected = true;
            store.Save(new AppPreferences { Awards = new AwardCatalog { Programs = new List<AwardProgram> { award } } });
            string file = Path.Combine(directory, "preferences.xml");
            const string legacy = "<Hamlog><Points>999</Points><Required>1</Required></Hamlog><OtherLevel>true</OtherLevel>";
            string xml = File.ReadAllText(file);
            Check(xml.Contains("</AwardProgram>"), "The migration fixture did not serialize an award.");
            File.WriteAllText(file, xml.Replace("</AwardProgram>", legacy + "</AwardProgram>"));
            AwardProgram loaded = store.Load().Awards.Programs.Single();
            Check(loaded.Selected && loaded.HasSearchRules && loaded.From.Value.Year == 2026,
                "Old credit prevented an otherwise available award from loading.");
            loaded.HasIssued = true;
            store.Save(new AppPreferences { Awards = new AwardCatalog { Programs = new List<AwardProgram> { loaded } } });
            File.WriteAllText(file, File.ReadAllText(file).Replace("</AwardProgram>", legacy + "</AwardProgram>"));
            Check(!store.Load().Awards.Programs.Single().Selected, "A legacy tier override re-enabled a received award.");
        });

        run("Published rules match live districts and current distance, band and mode", () => {
            DateTime day = new DateTime(2026, 11, 25, 5, 0, 0, DateTimeKind.Utc);
            var distance = new AwardProgram { Title = "Long distance", Url = "https://hamlog.online/club/demo/1100/", DownloadedRules = new HamlogRulePlan {
                Kind = "contact-points", FromUtc = "2014-03-21", Required = 100, MemberPoints = 1, ExtraPoints = 1,
                Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode, MinimumDistanceKm = 10000,
                    Bands = new[] { "20m" }, Modes = new[] { "DIGI" } }
            } };
            distance.DownloadedRules.Validate();
            distance.Validate();
            distance.Selected = true;
            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { distance } };
            var search = new AwardSearch(catalog, "K1ABC", "20m", "FT4", null, "KO85AA");
            Check(search.MatchCount("VK2BBB", day, "QF56") == 1 && search.MatchCount("VK2BBB", day, "RR73") == 0
                && search.MatchCount("DL1AAA", day, "JN58") == 0,
                "The distance rule did not use the two radio locators.");
            Check(new AwardSearch(catalog, "K1ABC", "40m", "FT4", null, "KO85AA").Available(day) == 0
                && new AwardSearch(catalog, "K1ABC", "20m", "SSB", null, "KO85AA").Available(day) == 0,
                "The rule ignored its band or mode restrictions.");
            search.SetOperatorLocator(null);
            Check(search.Available(day) == 0 && search.NeedsOperatorLocator(day) && search.MatchCount("VK2BBB", day, "QF56") == 0,
                "A missing JTDX locator left distance-based TX eligible.");
            Check(!AwardLocator.FartherThan("JJ00", "NJ60", 10000)
                && AwardLocator.FartherThan("JJ00AA", "NJ60AA", 10000), "A coarse grid was treated as an exact coordinate.");

            var national = new AwardProgram { Title = "National RDA", Url = "https://hamlog.online/club/demo/1101/", DownloadedRules = new HamlogRulePlan { Kind = "rda-search", Required = 1 } };
            var tula = HamlogAward.Gunsmiths.Create();
            var russianGrid = HamlogAward.Russia.Create();
            foreach (AwardProgram program in new[] { national, tula, russianGrid }) { program.Validate(); program.Selected = true; }
            catalog.Programs = new List<AwardProgram> { national, tula, russianGrid };
            Func<string, string> country = call => call.StartsWith("RA2") ? "UA2" : call.StartsWith("R0") ? "UA9"
                : call.StartsWith("R") ? "UA" : "K";
            var regions = new AwardSearch(catalog, "K1ABC", "20m", "FT8", country);
            Check(regions.MatchCount("R3PAA/P", day, "KO92") == 3 && regions.MatchCount("R3SAA", day, "KO92") == 2
                && regions.MatchCount("RA2FAA", day) == 1 && regions.MatchCount("R0AAA", day, "RR73") == 1
                && regions.MatchCount("W2AAA", day, "FN31") == 0, "Regional, national and locator rules selected the wrong DX.");
        });

        run("RCPW uses the bundled club callsigns and the published activity window", () => {
            var plan = new HamlogRulePlan { Kind = "rcpw-hunter", FromUtc = "2023-05-28T00:00:00Z",
                BonusUntilUtc = "2023-05-30T00:00:00Z", Required = 105, AlternativeRequired = 50,
                MemberPoints = 5, ExtraPoints = 10, RcpwSpecialPoints = 10, Calls = new[] { "R105PW" } };
            plan.Validate();
            var award = new AwardProgram { Title = "Club activity", Url = "https://hamlog.online/club/demo/1102/", DownloadedRules = plan };
            award.Validate();
            award.Selected = true;
            var search = new AwardSearch(new AwardCatalog { Programs = new List<AwardProgram> { award } }, "K1ABC", "20m", "FT8", null);
            DateTime after = new DateTime(2023, 5, 30, 0, 0, 0, DateTimeKind.Utc);
            Check(search.MatchCount("R6DDF", after) == 1 && search.MatchCount("R105PW", after.AddSeconds(-1)) == 1
                && search.MatchCount("R105PW", after) == 0 && search.MatchCount("W2ABC", after) == 0,
                "A club member or temporary call was evaluated against the wrong criteria.");
        });

        run("Award monitoring keeps searching on live rules regardless of the logbook and QSO events", () => {
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
            var engine = new AutoCqEngine {
                DxccForCall = call => call.StartsWith("JA") ? "JA" : null,
                WorkedCallsOnBand = (band, call) => { throw new Exception("Read the logbook on award start."); },
                WorkedCallsOnBandMode = (band, mode, call) => { throw new Exception("Read the logbook on award start."); },
            };
            engine.Options.MonitorOnePerBand = true;
            var search = new AwardSearch(catalog, "K1AAA", "20m", "FT8", engine.DxccForCall);
            engine.OnStatus(new RadioStatus { Frequency = 14074000, Mode = "FT8", DeCall = "K1AAA", DxCall = "" }, at);
            var commands = new List<string>();
            engine.RequestCq = () => commands.Add("CQ");
            engine.RequestReply = decode => commands.Add(decode.Message);
            engine.Start(at, search);
            foreach (string message in new[] { "CQ JA2AA PM95", "CQ JA3ZZ PM96" })
                engine.OnDecode(new DecodedMessage { IsNew = true, Mode = "~", Message = message,
                    Milliseconds = (uint)at.TimeOfDay.TotalMilliseconds }, at.AddSeconds(14));
            engine.Tick(at.AddSeconds(14), true);
            Check(engine.Monitoring && commands.SequenceEqual(new[] { "CQ JA3ZZ PM96" }),
                "Award search started CQ or ignored a station matching more selected awards.");
            engine.OnStatus(new RadioStatus { Frequency = 14074000, Mode = "FT8", DeCall = "K1AAA", DxCall = "JA3ZZ", TxEnabled = true }, at.AddSeconds(15));
            engine.OnLine("20260115_120015.100(0) Transmitting 14.074 MHz + 1500Hz FT8: JA3ZZ K1AAA RR73", at.AddSeconds(15));
            engine.OnLine("20260115_120015.200(0) QSO logged: JA3ZZ", at.AddSeconds(15.2));
            Check(search.Available(at.AddSeconds(16)) == 2 && search.MatchCount("JA3ZZ", at.AddSeconds(16)) == 2,
                "A logged QSO changed the live rules or retired a selected award.");
        });
    }
}
