using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace JtdxAutoCq
{
    // SpotMachineServer writes batches of packed records, independently of the logbook.
    internal static class LogHxSpotFeed
    {
        const int RecordSize = 267;
        const int MaxBytes = RecordSize * 10000;
        const int CallOffset = 0, CommentOffset = 44, ModeOffset = 197, TimeOffset = 230, FrequencyOffset = 246;

        internal static ExpeditionFeedData Read(DateTime now)
        {
            string path = FindFile();
            byte[] bytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new BinaryReader(input))
            {
                long length = input.Length;
                if (length > MaxBytes || length % RecordSize != 0)
                    throw new InvalidDataException("Не удалось прочитать пакет спотов LogHX.");
                bytes = reader.ReadBytes((int)length);
                if (bytes.Length != length || input.Length != length)
                    throw new IOException("LogHX обновляет список спотов.");
            }
            return Parse(bytes, now);
        }

        static string FindFile()
        {
            Process[] processes = Process.GetProcessesByName("SpotMachineServer").Concat(Process.GetProcessesByName("LogHX3")).ToArray();
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        string folder = Path.GetDirectoryName(MainForm.RunningExecutable(process.Id));
                        if (String.IsNullOrEmpty(folder)) continue;
                        string path = Path.Combine(folder, "Temp", "SpotsDataList.hx");
                        if (File.Exists(path)) return path;
                    }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            finally { foreach (Process process in processes) process.Dispose(); }
            return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "LogHX", "Temp", "SpotsDataList.hx");
        }

        internal static ExpeditionFeedData Parse(byte[] bytes, DateTime now)
        {
            if (bytes.Length > MaxBytes || bytes.Length % RecordSize != 0)
                throw new InvalidDataException("Неизвестный формат пакета спотов LogHX.");
            var spots = new List<ExpeditionActivity>();
            double earliest = now.AddMinutes(-15).ToOADate(), latest = now.AddMinutes(1).ToOADate();
            for (int offset = 0; offset < bytes.Length; offset += RecordSize)
            {
                double stamp = BitConverter.ToDouble(bytes, offset + TimeOffset);
                if (!(stamp >= earliest && stamp <= latest)) continue;
                DateTime time = DateTime.SpecifyKind(DateTime.FromOADate(stamp), DateTimeKind.Utc);
                double hz = BitConverter.ToUInt32(bytes, offset + FrequencyOffset);
                ExpeditionActivity spot = ExpeditionFeeds.ClusterSpot(ExpeditionSource.LogHx,
                    ReadField(bytes, offset + CallOffset, 21), ReadField(bytes, offset + ModeOffset, 33),
                    hz, time, ReadField(bytes, offset + CommentOffset, 101));
                if (spot != null) spots.Add(spot);
            }
            return new ExpeditionFeedData { Activities = spots.ToArray() };
        }

        static string ReadField(byte[] bytes, int offset, int capacity)
        {
            int end = Array.IndexOf(bytes, (byte)0, offset, capacity);
            return Encoding.ASCII.GetString(bytes, offset, end < 0 ? capacity : end - offset).Trim();
        }
    }
}
