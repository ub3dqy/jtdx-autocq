using System;

namespace JtdxAutoCq
{
    public enum UpdateDownloadStage { Preparing, Connecting, Downloading, Verifying, Extracting, Ready }

    public sealed class UpdateDownloadProgress
    {
        public readonly UpdateDownloadStage Stage;
        public readonly long ReceivedBytes;
        public readonly long? TotalBytes;
        public readonly TimeSpan Elapsed;
        public UpdateDownloadProgress(UpdateDownloadStage stage, long receivedBytes = 0, long? totalBytes = null, TimeSpan elapsed = default(TimeSpan))
        {
            if (receivedBytes < 0 || totalBytes.HasValue && totalBytes.Value < 0 || elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException();
            Stage = stage; ReceivedBytes = receivedBytes; TotalBytes = totalBytes; Elapsed = elapsed;
        }
        public int? Percent
        { get { return TotalBytes.HasValue && TotalBytes.Value > 0 ? (int?)Math.Min(100, 100.0 * ReceivedBytes / TotalBytes.Value) : null; } }
        public double BytesPerSecond
        { get { return Elapsed.TotalSeconds > 0 ? ReceivedBytes / Elapsed.TotalSeconds : 0; } }
        public TimeSpan? Remaining
        {
            get
            {
                if (!TotalBytes.HasValue || BytesPerSecond <= 0) return null;
                return TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, TotalBytes.Value - ReceivedBytes) / BytesPerSecond));
            }
        }
        public UpdateDownloadProgress AtStage(UpdateDownloadStage stage)
        { return new UpdateDownloadProgress(stage, ReceivedBytes, TotalBytes, Elapsed); }
    }
}
