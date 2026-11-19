using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using JtdxAutoCq;

static class BugReportTests
{
    static void Assert(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }

    internal static void RunAll(Action<string, Action> run)
    {
        run("Bug report holds the 20 minutes before clicking across a UTC month boundary", () => {
            string folder = TestWorkspace.Create();
            string module = Path.Combine(folder, "runtime.log");
            var click = new DateTimeOffset(2026, 2, 1, 0, 10, 0, TimeSpan.Zero);
            string first = click.AddMinutes(-20).ToOffset(TimeSpan.FromHours(3)).ToString("O");
            File.WriteAllText(module,
                click.AddMinutes(-20).AddTicks(-1).ToString("O") + " Too old\n" +
                first + " Снимок начался\n" +
                click.AddMinutes(-1).ToString("O") + " TX COMMAND Reply message=CQ W2ABC FN31 endpoint=127.0.0.1:2247\n" +
                click.ToString("O") + " Ошибка записи D:\\radio\\private.ini\n",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "202601_ALL.TXT"),
                "20260131_234959 -12 0.2 1400 ~ CQ W1OLD FN31\n" +
                "20260131_235915 -14 0.1 1500 ~ CQ W2ABC FN31\n" + new string('x', 32768) + "\n");
            string current = Path.Combine(folder, "202602_ALL.TXT");
            File.WriteAllText(current, "20260201_000001.125(0)  Transmitting 7.074 MHz + 1500Hz FT8: W2ABC K1ABC -14\n" +
                "20260201_001001 -5 0.1 1300 ~ CQ W1NEW FN31\n" +
                "20260201_000958 -5 0.1 1300 ~ CQ W1PART FN31");
            var capture = new BugReportCapture(click, "0.9.13", "2.2.160-32A-rc10",
                new Dictionary<string, string> { { "mode", "FT8" } }, folder, folder);
            File.AppendAllText(module, click.AddSeconds(-1).ToString("O") + " Written after click\n");
            File.AppendAllText(current, "\n20260201_000959 -5 0.1 1300 ~ CQ W1LATER FN31\n");
            BugReportData report = capture.Read();
            Assert(DateTimeOffset.Parse(report.FromUtc) == click.AddMinutes(-20) && DateTimeOffset.Parse(report.ToUtc) == click,
                "The report window moved after the click.");
            Assert(report.AutoCqLog.Contains("Снимок начался") && report.AutoCqLog.Contains("CQ W2ABC FN31") &&
                !report.AutoCqLog.Contains("Too old") && !report.AutoCqLog.Contains("Written after click"),
                "The AutoCQ log used the wrong time or file boundary.");
            Assert(!report.AutoCqLog.Contains("127.0.0.1") && !report.AutoCqLog.Contains("private.ini"),
                "Private endpoint or file path was included.");
            Assert(report.JtdxLog.Contains("23:59:15") && report.JtdxLog.Contains("00:00:01.125") &&
                !report.JtdxLog.Contains("W1OLD") && !report.JtdxLog.Contains("W1NEW") &&
                !report.JtdxLog.Contains("W1PART") && !report.JtdxLog.Contains("W1LATER"),
                "The JTDX log included out-of-window or incomplete entries.");
            string encoded = Encoding.UTF8.GetString(report.Encode());
            Assert(encoded.Contains("\"state\":{\"mode\":\"FT8\"}") && encoded.Contains("\"requestId\""),
                "Diagnostics do not match the server schema.");
        });

        run("GitHub draft contains the operator's own text and report number only", () => {
            const string number = "ACQ-0123456789abcdef0123456789abcdef";
            string body = BugReportClient.IssueBody("Пропущен ответ +02\nПовторяется.", "0.9.13", number);
            string url = BugReportClient.DraftUrl("Пропуск ответа", body);
            Assert(url != null && url.StartsWith("https://github.com/ub3dqy/jtdx-autocq/issues/new?", StringComparison.Ordinal)
                && Uri.UnescapeDataString(url).Contains("Пропущен ответ +02"), "GitHub draft did not preserve the description.");
            Assert(body.Contains(number) && !url.Contains("jtdxLog") && !url.Contains("ub3dqy.com"),
                "The public draft contained private diagnostics.");
            Assert(BugReportClient.DraftUrl("Пример", new string('я', 2000)) == null,
                "A long description must use the copy-to-clipboard path.");
        });
    }
}
