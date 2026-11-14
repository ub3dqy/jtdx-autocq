using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    public sealed class CqStation
    {
        public string Call;
        public DecodedMessage Decode;
        public DateTime HeardUtc, LastAttemptUtc, NextTryUtc, LastActivityUtc;
        public bool Available, Worked, Busy;
        public string Activity, Signoff;
    }

    public sealed class HeardCqList
    {
        readonly List<CqStation> rows = new List<CqStation>();
        readonly HashSet<string> worked = new HashSet<string>();
        string context;
        public Action Changed = delegate { };
        public ReadOnlyCollection<CqStation> Rows { get { return rows.AsReadOnly(); } }
        public void SetContext(RadioStatus status)
        {
            string next = status.Frequency + "/" + status.Mode + "/" + Callsign.Clean(status.DeCall);
            if (next == context) return;
            rows.Clear(); worked.Clear(); context = next; Changed();
        }
        public void Invalidate()
        { foreach(CqStation row in rows)row.Available=false; Changed(); }
        public void InvalidateCall(string call)
        {CqStation row=Find(call);if(row==null)return;row.Available=false;Changed();}
        public void Observe(DecodedMessage decode, RadioStatus status, DateTime now)
        { Observe(decode, status, now, true); }

        public void Observe(DecodedMessage decode, RadioStatus status, DateTime now, bool visible)
        {
            if(status==null || decode==null || decode.OffAir || (status.Mode!="FT8" && status.Mode!="FT4"))return;
            if((status.Mode=="FT8" && decode.Mode!="~") || (status.Mode=="FT4" && decode.Mode!=":" && decode.Mode!="+"))return;
            string[] words = MessageWords(decode);
            DateTime time=decode.UtcTime(now);
            if(time>now.AddSeconds(2) || (now-time).TotalSeconds>120)return;
            if (words.Length < 2) return;
            string own = Callsign.Clean(status.DeCall);
            bool generalCall = words[0] == "CQ" || words[0] == "CQDX" || words[0] == "QRZ";
            if (generalCall && !visible) return;
            string call = null;
            if (generalCall)
            {
                for (int i = 1; i < words.Length && i < 4; i++)
                {
                    if (Regex.IsMatch(words[i], @"^[A-R]{2}\d{2}(?:[A-X]{2})?$")) continue;
                    call = Callsign.Parse(words[i]);
                    if (call != null) break;
                }
            }
            else if (words.Length >= 3)
            {
                call = Callsign.Parse(words[1]);
            }
            if (call == null || call == own) return;

            string recipient = Callsign.Clean(words[0]);
            string ownBase = BaseCall(own);
            string recipientBase = BaseCall(recipient);
            bool thirdParty = recipientBase != ownBase && BaseCall(call) != ownBase && recipient != call
                && (words[0] == "<...>" || Callsign.Parse(recipient) != null);
            string signoff = !generalCall && thirdParty && words.Length == 3
                && (words[2] == "73" || words[2] == "RRR" || words[2] == "RR73") ? words[2] : null;
            CqStation row = Find(call);
            if (signoff != null && !visible)
            {
                if (row != null && time > row.LastActivityUtc)
                {
                    row.Busy = true;
                    row.LastActivityUtc = time;
                    row.Activity = "Ждёт появления " + signoff + " в левом окне";
                    Changed();
                }
                return;
            }
            if (row == null && !generalCall && signoff == null) return;
            if (row == null)
            {
                row = new CqStation { Call = call, Worked = worked.Contains(call) };
                rows.Add(row);
            }
            if (row.LastActivityUtc > time) return;
            if (!generalCall && signoff == null)
            {
                row.Busy = true;
                row.LastActivityUtc = time;
                row.Activity = recipient == own ? "Вызывает вас" : "Занят другой связью";
            }
            else
            {
                row.Available = true;
                row.Decode = decode.Copy();
                row.HeardUtc = row.LastActivityUtc = time;
                row.Signoff = signoff;
                row.Busy = false;
                row.Activity = null;
            }
            if (rows.Count > 200) rows.Remove(rows.OrderBy(r => r.HeardUtc).First());
            Changed();
        }

        static readonly char[] DecodeAnnotations = { '^', '*', '?', 'a' };

        // JTDX compares the longest part of a portable callsign with its own base call.
        static string BaseCall(string call)
        {
            int slash = call.IndexOf('/');
            if (slash < 0) return call;
            return call.Length - slash >= slash ? call.Substring(slash + 1) : call.Substring(0, slash);
        }

        static string[] MessageWords(DecodedMessage decode)
        {
            return (decode.Message ?? "").TrimEnd().TrimEnd(DecodeAnnotations).ToUpperInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public CqStation Find(string call)
        {return rows.FirstOrDefault(r=>r.Call==Callsign.Clean(call));}
        public bool Fresh(CqStation row,DateTime now)
        {
            if (row == null || !row.Available || row.Busy || row.Decode == null || row.HeardUtc > now.AddSeconds(2)) return false;
            // A sign-off points to the next opportunity to call. An older
            // sign-off is no substitute for a recent CQ from that station.
            double age = row.Signoff == null ? 120 : row.Decode.Mode == "~" ? 30.5 : 15.5;
            return now - row.HeardUtc <= TimeSpan.FromSeconds(age);
        }
        public bool CanTry(CqStation row, DateTime now, bool allowWorked = false)
        { return Fresh(row, now) && (allowWorked || !row.Worked) && now >= row.NextTryUtc; }

        public CqStation Next(DateTime now, Predicate<string> canCall = null, bool includeSignoffs = false,
            Predicate<CqStation> eligible = null, bool allowWorked = false)
        {
            // A recent CQ is more likely to remain actionable in JTDX and to
            // describe a station still calling. Keep the caller FIFO separate.
            return rows.Where(r=>CanTry(r, now, allowWorked) && (includeSignoffs || r.Signoff == null)
                    && (canCall == null || canCall(r.Call)) && (eligible == null || eligible(r)))
                .OrderByDescending(r=>r.HeardUtc).ThenBy(r=>r.LastAttemptUtc).FirstOrDefault();
        }
        public void Attempt(string call,DateTime now)
        {CqStation row=Find(call);if(row==null)return;row.LastAttemptUtc=now;row.NextTryUtc=now.AddMinutes(5);Changed();}
        public void Failed(string call,DateTime now)
        {CqStation row=Find(call);if(row==null)return;row.NextTryUtc=now.AddMinutes(5);Changed();}
        public void Complete(string call)
        {call=Callsign.Clean(call);worked.Add(call);CqStation row=Find(call);if(row==null)return;row.Worked=true;Changed();}
        public void Retry(string call)
        {CqStation row=Find(call);if(row==null)return;worked.Remove(row.Call);row.NextTryUtc=DateTime.MinValue;row.Worked=false;Changed();}
        public string State(CqStation row,DateTime now)
        {
            if(row.Worked)return "Связь записана";
            if(row.Busy)return row.Activity;
            if(!Fresh(row,now))return row.Signoff == null ? "Нет свежего CQ" : "Нет свежего " + row.Signoff;
            if(now<row.NextTryUtc)return "Повтор после "+row.NextTryUtc.ToLocalTime().ToString("HH:mm:ss");
            return "Можно вызвать";
        }
    }
}
