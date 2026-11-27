using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    internal sealed class DecodeSample
    {
        internal DateTime AtUtc;
        internal string Mode, Band;
        internal double? Duration, FromBoundary;
        internal int LostBlocks;

        internal DecodeSample Copy() { return (DecodeSample)MemberwiseClone(); }
    }

    internal sealed class DecodeDiagnostics
    {
        static readonly Regex NativeEvent = new Regex(
            @"^(?<time>\d{8}_\d{6}\.\d{3})\(\d+\)\s+(?:(?<end>Decoding finished)\b|Lost audio blocks:\s*(?<lost>\d{1,8}))",
            RegexOptions.CultureInvariant);
        readonly Queue<DecodeSample> history = new Queue<DecodeSample>();
        DateTime? began, decodedSlot;
        DecodeSample lastFinishedSample;
        bool decoding;
        DateTime lastFinish;
        ulong frequency;
        string mode;

        internal void Status(RadioStatus status, DateTime now)
        {
            bool different = status.Mode != mode || status.Frequency != frequency;
            if (different) { began = decodedSlot = null; decoding = false; lastFinish = DateTime.MinValue; lastFinishedSample = null; }
            mode = status.Mode;
            frequency = status.Frequency;
            if (status.Decoding && !decoding) began = now;
            decoding = status.Decoding;
        }

        internal void Decode(DecodedMessage message, DateTime now)
        {
            if (!message.IsNew || message.OffAir || message.Milliseconds >= 86400000) return;
            if (mode == "FT8" && message.Mode != "~" || mode == "FT4" && message.Mode != "+" && message.Mode != ":") return;
            if (mode != "FT8" && mode != "FT4") return;
            DateTime time = message.UtcTime(now);
            long slot = Period.Ticks;
            DateTime aligned = new DateTime((time.Ticks + TimeSpan.FromMilliseconds(500).Ticks) / slot * slot, DateTimeKind.Utc);
            if (aligned > now.AddSeconds(2) || now - aligned > Period + Period) return;
            DateTime boundary = aligned + Period;
            if (lastFinishedSample != null && now >= lastFinish && now - lastFinish <= TimeSpan.FromSeconds(3)
                && Math.Abs((lastFinish - boundary).TotalMilliseconds) < Period.TotalMilliseconds / 2)
            {
                lastFinishedSample.FromBoundary = (lastFinish - boundary).TotalMilliseconds;
                return;
            }
            if (!decodedSlot.HasValue || decodedSlot.Value < aligned) decodedSlot = aligned;
        }

        TimeSpan Period { get { return TimeSpan.FromSeconds(mode == "FT4" ? 7.5 : 15); } }

        internal void Line(string line, DateTime seenUtc)
        {
            if (mode != "FT8" && mode != "FT4") return;
            Match match = NativeEvent.Match(line ?? "");
            DateTime time;
            if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value, "yyyyMMdd_HHmmss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time)
                || time > seenUtc.AddSeconds(2) || seenUtc - time > TimeSpan.FromSeconds(30)) return;
            var sample = new DecodeSample { AtUtc = time, Mode = mode, Band = JtdxAdif.Band(frequency) };
            if (match.Groups["end"].Success)
            {
                if (time <= lastFinish) return;
                lastFinish = time;
                if (began.HasValue && time >= began.Value && time - began.Value < Period + Period)
                    sample.Duration = (time - began.Value).TotalMilliseconds;
                if (decodedSlot.HasValue && time >= decodedSlot.Value && time - decodedSlot.Value < Period + Period)
                    sample.FromBoundary = (time - decodedSlot.Value - Period).TotalMilliseconds;
                began = decodedSlot = null;
                lastFinishedSample = sample;
            }
            else sample.LostBlocks = Int32.Parse(match.Groups["lost"].Value, CultureInfo.InvariantCulture);
            history.Enqueue(sample);
            while (history.Count > 2000 || seenUtc - history.Peek().AtUtc > TimeSpan.FromMinutes(20))
                history.Dequeue();
        }

        internal DecodeSample[] Samples(DateTime now)
        { return history.Where(row => now - row.AtUtc <= TimeSpan.FromMinutes(5)).ToArray(); }
    }
}
