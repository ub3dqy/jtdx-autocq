using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JtdxAutoCq;

static class NewOperatorToolsTests
{
    static readonly DateTime Noon = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    static void Require(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    internal static void RunAll(Action<string, Action> run)
    {
        run("PSK Reporter selects recent FT8 and FT4 reports for this sender", Reporter);
        run("JTDX decoding measures FT8 and FT4 boundaries in either arrival order", Decoding);
        run("Session record round trips and compares commands with an isolated engine", Trace);
    }

    static void Reporter()
    {
        long current = (long)(Noon - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        Func<string, string, long, string, string> item = (sender, mode, time, snr) =>
            String.Format(CultureInfo.InvariantCulture,
                "<receptionReport senderCallsign='{0}' mode='{1}' flowStartSeconds='{2}' receiverCallsign='JA1AAA' receiverLocator='pm95ab' frequency='14074000' {3}/>",
                sender, mode, time, snr);
        string xml = "<receptionReports>" + item("K1ABC", "FT4", current - 10, "sNR='-11'")
            + item("K1ABC", "FT8", current - 15, "")
            + item("K2ABC", "FT8", current - 20, "") + item("K1ABC", "FT8", current - 3601, "")
            + item("K1ABC", "FT8", current + 120, "") + item("K1ABC", "CW", current - 25, "") + "</receptionReports>";
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
        {
            ReceptionReport[] reports = PskReporter.Parse(input, "K1ABC", Noon);
            Require(reports.Length == 2 && reports[0].Mode == "FT4" && reports[0].Band == "20m"
                && reports[0].Grid == "PM95AB" && reports[0].Snr == -11
                && reports[1].Mode == "FT8" && !reports[1].Snr.HasValue, "Unexpected reports.");
        }
    }

    static string Native(DateTime time, string message)
    {
        return time.ToString("yyyyMMdd_HHmmss.fff", CultureInfo.InvariantCulture) + "(0)  " + message;
    }

    static void Decoding()
    {
        foreach (string mode in new[] { "FT8", "FT4" })
        {
            var diagnostics = new DecodeDiagnostics();
            TimeSpan period = TimeSpan.FromSeconds(mode == "FT8" ? 15 : 7.5);
            DateTime finish = Noon + period + TimeSpan.FromMilliseconds(120);
            var status = new RadioStatus { Frequency = 14074000, Mode = mode, Decoding = true };
            diagnostics.Status(status, finish.AddMilliseconds(-700));
            var decoded = new DecodedMessage { IsNew = true, Milliseconds = (uint)Noon.TimeOfDay.TotalMilliseconds,
                Mode = mode == "FT8" ? "~" : "+" };
            diagnostics.Decode(decoded, finish.AddMilliseconds(-30));
            diagnostics.Line(Native(finish, "Decoding finished"), finish);
            DateTime next = Noon + period + period;
            DateTime early = next + period - TimeSpan.FromMilliseconds(420);
            diagnostics.Line(Native(early, "Decoding finished"), early);
            decoded.Milliseconds = (uint)next.TimeOfDay.TotalMilliseconds;
            diagnostics.Decode(decoded, early.AddMilliseconds(50));
            diagnostics.Line(Native(early.AddMilliseconds(100), "Lost audio blocks: 6"), early.AddMilliseconds(100));
            DecodeSample[] rows = diagnostics.Samples(early.AddSeconds(1));
            Require(rows.Length == 3 && rows[0].Duration == 700 && rows[0].FromBoundary == 120
                && rows[1].FromBoundary == -420 && !rows[1].Duration.HasValue && rows[2].LostBlocks == 6,
                mode + " decode measurements differed.");
            Require(diagnostics.Samples(early.AddMinutes(6)).Length == 0, "The rolling period did not expire.");
        }
    }

    static void Trace()
    {
        var trace = new SessionTrace {
            FirstStatus = new RadioStatus { Mode = "FT8", Frequency = 14074000, DeCall = "K1ABC" },
            StartUtc = Noon, Options = new AutoCqOptions(),
            Frames = new[] {
                new TraceFrame { AtUtc = Noon.AddSeconds(2), Kind = TraceEvent.Timer, Connected = true },
                new TraceFrame { AtUtc = Noon.AddSeconds(2), Kind = TraceEvent.Command, Text = "Общий вызов" }
            }
        };
        string path = Path.Combine(TestWorkspace.Create(), "session.xml");
        trace.Save(path);
        SessionTrace opened = SessionTrace.Read(path);
        string result = opened.Replay(opened.Options, call => "K");
        Require(result.StartsWith("Расхождений в командах нет") && result.Contains("— 1."), result);
        var changed = opened.Options.Copy();
        changed.MonitorEnabled = true;
        changed.MonitorCalls = new[] { "JA1AAA" };
        Require(opened.Replay(changed, call => "K").StartsWith("Первое расхождение"), "Changed profile was not detected.");
    }
}
