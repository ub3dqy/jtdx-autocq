using System;
using System.Collections.Generic;
using System.Linq;

namespace JtdxAutoCq
{
    public sealed class OperatorProfile
    {
        public string Name;
        public AutoCqOptions Options = new AutoCqOptions();
    }

    public sealed class SessionContact
    {
        public string Call;
        public DateTime? LoggedUtc, ConfirmedUtc, MissingUtc;
        public DateTime? TimeUtc { get { return LoggedUtc ?? ConfirmedUtc ?? MissingUtc; } }
    }

    public sealed class OperatingSession
    {
        public DateTime StartedUtc;
        public DateTime? StoppedUtc;
        public string OwnCall, Band, Mode, StopReason;
        public bool Monitoring;
        public List<SessionContact> Contacts = new List<SessionContact>();

        internal OperatingSession Snapshot()
        {
            var copy = (OperatingSession)MemberwiseClone();
            copy.Contacts = Contacts.Select(row => new SessionContact {
                Call = row.Call, LoggedUtc = row.LoggedUtc, ConfirmedUtc = row.ConfirmedUtc, MissingUtc = row.MissingUtc,
            }).ToList();
            return copy;
        }

        internal HashSet<SessionContact> FindIn(LogbookHistory history)
        {
            var matched = new HashSet<SessionContact>();
            var used = new HashSet<LogbookQso>();
            var byCall = history.Records.Where(qso => qso.BelongsTo(OwnCall) && qso.Band == Band && qso.Mode == Mode)
                .ToLookup(qso => qso.Call);
            var pairs =
                from contact in Contacts
                where contact.TimeUtc.HasValue
                from record in byCall[contact.Call]
                let time = record.EndedUtc ?? record.StartedUtc
                where time.HasValue && time.Value >= StartedUtc.AddSeconds(-59)
                let distance = Math.Abs((time.Value - contact.TimeUtc.Value).TotalSeconds)
                where distance <= 90
                let differentMinute = history.IsLogHx
                    && time.Value.Ticks / TimeSpan.TicksPerMinute != contact.TimeUtc.Value.Ticks / TimeSpan.TicksPerMinute
                orderby differentMinute, distance
                select new { Contact = contact, Record = record };
            foreach (var pair in pairs)
            {
                if (!matched.Contains(pair.Contact) && used.Add(pair.Record)) matched.Add(pair.Contact);
            }
            return matched;
        }
    }
}
