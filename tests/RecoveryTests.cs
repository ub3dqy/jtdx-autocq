using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JtdxAutoCq;

static class RecoveryTests
{
    static readonly DateTime When = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    static OperatingSession Session(string mode = "FT8")
    {
        return new OperatingSession { OwnCall = "K1ABC", Band = "20m", Mode = mode,
            StartedUtc = When, StoppedUtc = When.AddMinutes(15) };
    }

    static LogbookHistory Target(params LogbookQso[] records)
    { return new LogbookHistory("main.lhx2", records.ToList()); }

    static string Record(string call, string start, string end, string mode = "<MODE:3>FT8", string own = "K1ABC")
    {
        return "<CALL:" + call.Length + ">" + call + "<BAND:3>20m" + mode
            + "<STATION_CALLSIGN:" + own.Length + ">" + own + "<QSO_DATE:8>20260115<TIME_ON:6>" + start
            + "<QSO_DATE_OFF:8>20260115<TIME_OFF:6>" + end
            + "<FREQ:6>14.074<RST_SENT:3>-13<RST_RCVD:3>-04<GRIDSQUARE:4>JO20<COMMENT:10>hello<EOR><EOR>";
    }

    internal static void RunAll(Action<string, Action> run)
    {
        run("Recovery exports only physical JTDX records matched to logged session events", () => {
            var session = Session();
            session.Contacts.Add(new SessionContact { Call = "DL1AAA", LoggedUtc = When.AddMinutes(4).AddMilliseconds(250) });
            session.Contacts.Add(new SessionContact { Call = "DL2AAA", ConfirmedUtc = When.AddMinutes(6) });
            session.Contacts.Add(new SessionContact { Call = "DL3AAA", LoggedUtc = When.AddMinutes(8) });
            session.Contacts.Add(new SessionContact { Call = "DL4AAA", LoggedUtc = When.AddMinutes(10) });
            string real = Record("DL1AAA", "120301", "120400");
            var native = JtdxAdif.ContactsFromText("<ADIF_VER:5>3.1.4<EOH>\r\n" + real + real
                + Record("DL2AAA", "120501", "120600") + Record("DL3AAA", "120710", "120800", own: "W1AAA")
                + Record("DL4AAA", "120920", "121000", "<MODE:4>MFSK<SUBMODE:3>FT4")
                + Record("DL1AAA", "130301", "130400"), preserveRecords: true);
            LogbookQso[] found = JtdxLogbookRecovery.FindMissing(session, Target(), native);
            Check(found.Length == 1 && found[0].Call == "DL1AAA" && found[0].RawAdif == real,
                "Recovery accepted an inferred, duplicate, different operator/mode or out-of-session contact, or changed its ADIF fields.");
            Check(JtdxAdif.ContactsFromText(real).Single().RawAdif == null,
                "The ordinary logbook read retained private raw ADIF fields.");
        });

        run("Recovery rechecks a newly written LogHX contact without blocking a genuine repeat", () => {
            var session = Session("FT4");
            session.Contacts.Add(new SessionContact { Call = "DL1AAA", LoggedUtc = When.AddMinutes(4).AddSeconds(9) });
            session.Contacts.Add(new SessionContact { Call = "DL1AAA", LoggedUtc = When.AddMinutes(9).AddSeconds(39) });
            const string mode = "<MODE:4>MFSK<SUBMODE:3>FT4";
            string first = Record("DL1AAA", "120333", "120409", mode);
            string repeat = Record("DL1AAA", "120847", "120939", mode);
            var native = JtdxAdif.ContactsFromText(first + repeat, preserveRecords: true);
            Check(JtdxLogbookRecovery.FindMissing(session, Target(), native).Length == 2, "A distinct repeat was treated as a duplicate.");

            var imported = new LogbookQso {
                Call = "DL1AAA", Station = "K1ABC", Band = "20m", Mode = "MFSK", StartedUtc = When.AddMinutes(3)
            };
            var verified = JtdxLogbookRecovery.FindMissing(session, Target(imported), native);
            Check(verified.Length == 1 && verified[0].RawAdif == repeat,
                "Recovery failed to detect a late import with minute precision, or suppressed a distinct repeat.");
            imported.StartedUtc = null;
            Check(JtdxLogbookRecovery.FindMissing(session, Target(imported), native).Length == 0,
                "Recovery offered possible duplicates from a journal record without a time.");
            Check(!JtdxLogbookRecovery.CanCompare(session, new LogbookHistory("cached-export.adi", native)),
                "Recovery treated an offline ADIF as the verified LogHX journal.");
        });

        run("Recovery writes a standalone ADIF and preserves existing journals when asked to overwrite", () => {
            string raw = Record("DL1AAA", "120333", "120409", "<MODE:4>MFSK<SUBMODE:3>FT4");
            var qso = JtdxAdif.ContactsFromText(raw, preserveRecords: true).Single();
            string path = Path.Combine(TestWorkspace.Create(), "recover.adi");
            Check(JtdxLogbookRecovery.Save(path, new[] { qso, qso }) == 1,
                "The ADIF writer duplicated a selected record.");
            string written = File.ReadAllText(path);
            var record = JtdxAdif.ContactsFromText(written, preserveRecords: true).Single();
            Check(written.StartsWith("<ADIF_VER:5>3.1.4<PROGRAMID:6>AutoCQ<EOH>") && record.Mode == "FT4" && record.RawAdif == raw,
                "The recovered ADIF lost native fields or its complete header.");
            bool failed = false;
            try { JtdxLogbookRecovery.Save(path, new[] { qso }); }
            catch (IOException) { failed = true; }
            Check(failed && File.ReadAllText(path) == written, "The exporter overwrote an existing ADIF.");
        });
    }
}
