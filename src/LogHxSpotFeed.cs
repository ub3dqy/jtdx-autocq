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
            bool serverRunning;
            string path = FindFile(out serverRunning);
            return ReadBatch(path, now, serverRunning);
        }

        internal static ExpeditionFeedData ReadBatch(string path, DateTime now, bool serverRunning)
        {
            byte[] bytes;
            try
            {
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
            }
            catch (FileNotFoundException)
            {
                if (!serverRunning) throw;
                // LogHX removes the file after consuming a batch. Poll for the next one.
                return new ExpeditionFeedData();
            }
            catch (IOException error)
            {
                const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);
                if (!serverRunning || (error.HResult != SharingViolation && error.HResult != LockViolation)) throw;
                // SpotMachineServer may hold the batch open while writing.
                return new ExpeditionFeedData();
            }
            return Parse(bytes, now);
        }

        static string FindFile(out bool serverRunning)
        {
            Process[] servers = Process.GetProcessesByName("SpotMachineServer");
            serverRunning = servers.Length > 0;
            Process[] processes = servers.Concat(Process.GetProcessesByName("LogHX3")).ToArray();
            string batchPath = null;
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        string folder = Path.GetDirectoryName(MainForm.RunningExecutable(process.Id));
                        if (String.IsNullOrEmpty(folder)) continue;
                        string temp = Path.Combine(folder, "Temp");
                        string path = Path.Combine(temp, "SpotsDataList.hx");
                        if (File.Exists(path)) return path;
                        if (batchPath == null && Directory.Exists(temp)) batchPath = path;
                    }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            finally { foreach (Process process in processes) process.Dispose(); }
            return batchPath ?? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "LogHX", "Temp", "SpotsDataList.hx");
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
