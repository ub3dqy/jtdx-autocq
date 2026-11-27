using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using JtdxAutoCq;

static class ListeningTests
{
    static readonly DateTime At = new DateTime(2025, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static RadioStatus Radio(string mode = "FT8")
    {
        return new RadioStatus { Frequency = 14074000, Mode = mode, DeCall = "K1ABC/P" };
    }

    static DecodedMessage Decode(string message, DateTime time, string mode = "~")
    {
        return new DecodedMessage { IsNew = true, Mode = mode, Message = message, DeltaFrequency = 1500,
            Milliseconds = (uint)time.TimeOfDay.TotalMilliseconds };
    }

    static ListeningWatch Watch(ListeningOptions options, AutoCqOptions targets = null,
        ExpeditionOptions expeditions = null, ExpeditionCatalog catalog = null)
    {
        return new ListeningWatch(options, targets ?? new AutoCqOptions(), expeditions ?? new ExpeditionOptions(),
            catalog ?? new ExpeditionCatalog(), call => call.StartsWith("JA", StringComparison.Ordinal) ? "JA" : null,
            call => call != "JA3AAA");
    }

    internal static void RunAll(Action<string, Action> run)
    {
        run("Listening filters real FT8 and FT4 decodes by DXCC, calls, masks and exclusions", () => {
            foreach (string mode in new[] { "FT8", "FT4" })
            {
                string marker = mode == "FT8" ? "~" : ":";
                var watch = Watch(new ListeningOptions(), new AutoCqOptions {
                    MonitorDxcc = new[] { "JA" }, MonitorCalls = new[] { "DL1AAA" }, MonitorMasks = new[] { "W2*" }
                });
                var radio = Radio(mode);
                foreach (string call in new[] { "JA1AAA", "DL1AAA", "W2ABC" })
                {
                    ListeningHit hit = watch.Observe(Decode("CQ " + call + " FN31", At, marker), radio, At.AddSeconds(7));
                    Check(hit != null && !hit.External && hit.Call == call && hit.Mode == mode && hit.TimeUtc == At,
                        "No local alert for the matched station " + call + " in " + mode);
                }
                foreach (string message in new[] { "CQ JA3AAA PM95", "CQ DL2AAA JN58", "QRZ W2ZZZ FN31" })
                    Check(watch.Observe(Decode(message, At, marker), radio, At.AddSeconds(7)) == null, "Unexpected match " + message);

                var own = Watch(new ListeningOptions(), new AutoCqOptions { MonitorMasks = new[] { "K*" } });
                Check(own.Observe(Decode("CQ K1ABC FN31", At, marker), radio, At.AddSeconds(7)) == null,
                    "A portable operator matched their base call");
            }
        });

        run("Listening ignores replays, stale messages, other modes, low confidence and transmission", () => {
            var watch = Watch(new ListeningOptions(), new AutoCqOptions { MonitorCalls = new[] { "W2ABC" } });
            var radio = Radio();
            const string message = "CQ W2ABC FN31";
            var replay = Decode(message, At); replay.IsNew = false;
            var offAir = Decode(message, At); offAir.OffAir = true;
            var low = Decode(message, At); low.LowConfidence = true;
            var invalid = Decode(message, At); invalid.Milliseconds = 86400000;
            foreach (DecodedMessage decode in new[] { replay, offAir, low, invalid, Decode(message, At, ":"),
                Decode(message, At.AddSeconds(-40)), Decode(message, At.AddSeconds(20)) })
                Check(watch.Observe(decode, radio, At.AddSeconds(7)) == null, "A non-live decode triggered an alert");
            radio.TxEnabled = true;
            Check(watch.Observe(Decode(message, At), radio, At.AddSeconds(7)) == null, "TX armed did not suspend the listener");
            radio.TxEnabled = false;
            radio.Transmitting = true;
            Check(watch.Observe(Decode(message, At), radio, At.AddSeconds(7)) == null, "Transmission did not suspend the listener");
            radio.Transmitting = false;
            Check(watch.Observe(Decode(message, At), radio, At.AddSeconds(7)) != null, "Listening did not resume on RX");
        });

        run("Listening uses a verified locator for RR73 and removes it when the receive context resets", () => {
            var watch = Watch(new ListeningOptions { UseMonitorTargets = false, LocatorPrefixes = new[] { "fn", "FN31" } });
            var radio = Radio();
            Check(watch.Observe(Decode("DL1AAA W2ABC FN31", At), radio, At.AddSeconds(12)) == null,
                "A regular QSO message was offered as a target");
            string ending = "DL1AAA W2ABC RR73";
            ListeningHit hit = watch.Observe(Decode(ending, At.AddSeconds(15)), radio, At.AddSeconds(29));
            Check(hit != null && hit.Call == "W2ABC" && hit.Details.Contains("Локатор FN31"), "RR73 lost the prior locator");
            Check(watch.Observe(Decode("DL1AAA W3ABC RR73", At.AddSeconds(15)), radio, At.AddSeconds(29)) == null,
                "RR73 was mistaken for a locator or inherited another station's data");
            watch.ResetDecodes();
            Check(watch.Observe(Decode(ending, At.AddMinutes(1)), radio, At.AddMinutes(1).AddSeconds(12)) == null,
                "A locator survived the receive reset");
            Check(ListeningOptions.NormalizeLocators(new[] { "ko", "KO", "KO85", "KO85AA" }).Length == 3,
                "Locale prefixes were not normalized");
            try
            {
                ListeningOptions.NormalizeLocators(new[] { "ZZ11" });
                throw new Exception("Invalid locator was accepted");
            }
            catch (InvalidOperationException) { }
        });

        run("Listening separates WWFF backup spots from actual decodes and suppresses duplicate alerts", () => {
            var catalog = new ExpeditionCatalog();
            var spot = new ExpeditionActivity {
                Call = "DL1AAA/P", Frequency = 14074000, Mode = "FT8", Source = ExpeditionSource.WwffSpothole,
                Place = "DLFF-1234", SpottedUtc = At
            };
            catalog.Replace(ExpeditionSource.Wwff, new ExpeditionFeedData { Activities = new[] { spot } });
            var watch = Watch(new ListeningOptions { UseMonitorTargets = false, UseExpeditions = true }, null, null, catalog);
            ListeningHit external = watch.ObserveSpots("K1ABC/P", At.AddSeconds(1)).Single();
            Check(external.External && external.Source.Contains("Spothole"), "The backup spot was not marked as an external alert");
            var radio = Radio();
            ListeningHit local = watch.Observe(Decode("CQ DL1AAA/P JN58", At), radio, At.AddSeconds(12));
            Check(local != null && !local.External && local.Source == "Декод JTDX", "External spot hid a real local decode");
            Check(watch.Observe(Decode("CQ DL1AAA/P JN58", At.AddSeconds(15)), radio, At.AddSeconds(27)) == null,
                "A repeated CQ produced a duplicate alert");
            Check(watch.ObserveSpots("K1ABC/P", At.AddMinutes(6)).Count() == 0, "Old cached spot produced another alert");
            spot.SpottedUtc = At.AddMinutes(7);
            Check(watch.ObserveSpots("K1ABC/P", At.AddMinutes(7).AddSeconds(1)).Count() == 1,
                "Fresh spot remained muted after the repeat pause");
        });

        run("Listening is off on launch and pauses across an automated session", () => {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(MainForm);
            using (var form = new MainForm(true))
            {
                ((Timer)type.GetField("timer", flags).GetValue(form)).Stop();
                var engine = (AutoCqEngine)type.GetField("engine", flags).GetValue(form);
                var recorded = (IList)type.GetField("heardTargets", flags).GetValue(form);
                MethodInfo observe = type.GetMethod("ObserveListeningDecode", flags);
                MethodInfo tick = type.GetMethod("UpdateListening", flags);
                engine.Options.MonitorCalls = new[] { "W2AAA", "W2BBB", "W2CCC" };
                observe.Invoke(form, new object[] { Decode("CQ W2AAA FN31", At), At.AddSeconds(12) });
                Check(recorded.Count == 0, "A launched application silently started listening");
                type.GetMethod("StartListening", flags).Invoke(form, new object[] { new ListeningOptions() });
                observe.Invoke(form, new object[] { Decode("CQ W2AAA FN31", At), At.AddSeconds(12) });
                Check(recorded.Count == 1, "The explicitly started listener did not observe a decode");
                engine.Start(At.AddSeconds(15));
                tick.Invoke(form, new object[] { At.AddSeconds(16) });
                observe.Invoke(form, new object[] { Decode("CQ W2BBB FN31", At.AddSeconds(15)), At.AddSeconds(27) });
                Check(recorded.Count == 1, "The listener generated alerts during an automated session");
                engine.Stop("fixture", false);
                tick.Invoke(form, new object[] { At.AddSeconds(31) });
                observe.Invoke(form, new object[] { Decode("CQ W2CCC FN31", At.AddSeconds(30)), At.AddSeconds(42) });
                Check(recorded.Count == 2 && !engine.Armed && engine.ReplyCommands == 0,
                    "The listener did not resume safely when automation stopped");
                type.GetMethod("StopListening", flags).Invoke(form, null);
            }
        });
    }
}
