using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Execute the assembled validation routines in a disposable process. Load
// only the Qt DLL used for QString, with no JTDX process or radio connection.
static class TestNativeQueue
{
    const int StubCodeBytes = 0x2000;
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll")]
    static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")]
    static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr history);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Sender(int configured, int port, uint ip, int ok);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Line(IntPtr value, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Control(IntPtr value, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Lease(ulong now);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReplyGate(int marker, int originalPrompt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NativeDecision(IntPtr mainWindow);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FromUtf8(IntPtr output, IntPtr input, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Left(IntPtr output, IntPtr input, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Mid(IntPtr output, IntPtr input, int start, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IndexOf(IntPtr value, IntPtr query, int start, int caseSensitivity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SignoffLogGate(int callIndex);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RecentCallLogGate(IntPtr mainWindow, IntPtr stringEquals);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReportLog(IntPtr mainWindow, IntPtr sentReport, int historyStatus, IntPtr startSeconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HistoryUpdate(int incomingTime, int savedTime, int incomingStatus, int savedStatus, IntPtr history, IntPtr currentDx, IntPtr peer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DirectCall(IntPtr window, IntPtr call, int marker);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void UiAction(IntPtr receiver);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void UiText(IntPtr receiver, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UiValue(IntPtr receiver);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void UiSetValue(IntPtr receiver, int value);

    static T Function<T>(IntPtr pointer) where T : class
    { return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T; }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS  " + message);
    }

    static IntPtr Find(IntPtr module, string name)
    {
        IntPtr pointer = GetProcAddress(module, name);
        if (pointer == IntPtr.Zero) throw new Exception("Missing export " + name);
        return pointer;
    }

    static int VerifyLine(Line line, string text)
    {
        IntPtr buffer = Marshal.StringToHGlobalUni(text);
        try { return line(buffer, text.Length); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static void Send(Control control, byte[] packet)
    {
        IntPtr buffer = Marshal.AllocHGlobal(packet.Length);
        try
        {
            Marshal.Copy(packet, 0, buffer, packet.Length);
            control(buffer, packet.Length);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static string QtString(IntPtr value)
    {
        IntPtr data = Marshal.ReadIntPtr(value);
        return Marshal.PtrToStringUni(IntPtr.Add(data, (int)Marshal.ReadInt64(data, 16)), Marshal.ReadInt32(data, 4));
    }

    static void TestQt(string dllDirectory)
    {
        if (!SetDllDirectory(dllDirectory)) throw new Exception("Cannot set private Qt DLL directory");
        IntPtr qt = LoadLibraryEx(Path.Combine(dllDirectory, "Qt5Core.dll"), IntPtr.Zero, 8);
        if (qt == IntPtr.Zero) throw new Exception("Cannot load installed Qt5Core: " + Marshal.GetLastWin32Error());
        FromUtf8 from = Function<FromUtf8>(Find(qt, "_ZN7QString15fromUtf8_helperEPKci"));
        Left left = Function<Left>(Find(qt, "_ZNK7QString4leftEi"));
        Mid mid = Function<Mid>(Find(qt, "_ZNK7QString3midEii"));
        const string example = "120015 -18  0.2 1004 ~ K1ABC SV9AUP KM25";
        byte[] bytes = Encoding.ASCII.GetBytes(example);
        IntPtr source = Marshal.AllocHGlobal(bytes.Length);
        IntPtr objects = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(bytes, 0, source, bytes.Length);
            from(objects, source, bytes.Length);
            left(IntPtr.Add(objects, 8), objects, -1);
            mid(IntPtr.Add(objects, 16), objects, 0, -1);
            Check(QtString(objects) == example && QtString(IntPtr.Add(objects, 8)) == example
                && QtString(IntPtr.Add(objects, 16)) == example,
                "Installed Qt copies the complete saved row, including the final grid character");
        }
        finally { Marshal.FreeHGlobal(objects); Marshal.FreeHGlobal(source); }
    }

    static void TestSignoffLogging(IntPtr image, string dllDirectory)
    {
        int gateRva = 0x110e0c;
        byte[] instructions = new byte[12];
        Marshal.Copy(IntPtr.Add(image, gateRva), instructions, 0, instructions.Length);
        Check(instructions[7] == 0xe9 && gateRva + instructions.Length + BitConverter.ToInt32(instructions, 8) == 0x10f4cf,
            "The sign-off gate returns to the original JTDX logging path");

        IntPtr qt = LoadLibraryEx(Path.Combine(dllDirectory, "Qt5Core.dll"), IntPtr.Zero, 8);
        if (qt == IntPtr.Zero) throw new Exception("Cannot load Qt for the sign-off fixture");
        IntPtr objects = Marshal.AllocHGlobal(16);
        IntPtr fixture = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
        if (fixture == IntPtr.Zero) throw new Exception("Cannot allocate the sign-off fixture");
        try
        {
            FromUtf8 from = Function<FromUtf8>(Find(qt, "_ZN7QString15fromUtf8_helperEPKci"));
            IndexOf indexOf = Function<IndexOf>(Find(qt, "_ZNK7QString7indexOfERKS_iN2Qt15CaseSensitivityE"));

            byte[] prologue = { 0x41, 0x56, 0x89, 0xc8 }; // save r14; move callIndex into eax
            Marshal.Copy(prologue, 0, fixture, prologue.Length);
            Marshal.Copy(instructions, 0, IntPtr.Add(fixture, prologue.Length), 7); // cmp/setcc from the JTDX image
            byte[] result = { 0x41, 0x0f, 0xb6, 0xc6, 0x41, 0x5e, 0xc3 }; // return r14b and restore r14
            Marshal.Copy(result, 0, IntPtr.Add(fixture, prologue.Length + 7), result.Length);
            uint previous;
            if (!VirtualProtect(fixture, (UIntPtr)4096, 0x20, out previous)) throw new Exception("Cannot protect sign-off code");
            SignoffLogGate mayLog = Function<SignoffLogGate>(fixture);

            IntPtr currentDx = IntPtr.Add(objects, 8);
            NewQtString(from, currentDx, "DL1ZZA");
            NewQtString(from, objects, "R9ZZA K1ABC RR73");
            int different = indexOf(objects, currentDx, 0, 1);
            Check(different == -1 && mayLog(different) == 1,
                "RR73 to two different callsigns reaches autologging for the second callsign");
            NewQtString(from, objects, "DL1ZZA K1ABC RR73");
            int same = indexOf(objects, currentDx, 0, 1);
            Check(same == 0 && mayLog(same) == 0,
                "A repeated RR73 to the same DX remains excluded from autologging");
        }
        finally { VirtualFree(fixture, UIntPtr.Zero, 0x8000); Marshal.FreeHGlobal(objects); FreeLibrary(qt); }
    }

    static void NewQtString(FromUtf8 from, IntPtr output, string value)
    {
        IntPtr source = Marshal.StringToHGlobalAnsi(value);
        try { from(output, source, value.Length); }
        finally { Marshal.FreeHGlobal(source); }
    }

    static void CopyImageCode(IntPtr source, IntPtr target, int rva, int length)
    {
        byte[] bytes = new byte[length];
        Marshal.Copy(IntPtr.Add(source, rva), bytes, 0, length);
        WriteCode(target, rva, bytes);
    }

    static void TestRepeatLogging(IntPtr image, string dllDirectory)
    {
        const int codeRva = 0x2d17000;
        const int firstGate = 0x10f4a0, reject = 0x10f4cc, secondGate = 0x110dd0, signoff = 0x110de9;
        const int exchangeStart = 0x2b58, lastLogged = 0x760, history = 0x2b70;
        const long day = 86400000L;
        const long utcMidnight = 20000L * day;
        IntPtr qt = LoadLibraryEx(Path.Combine(dllDirectory, "Qt5Core.dll"), IntPtr.Zero, 8);
        IntPtr fixture = VirtualAlloc(IntPtr.Zero, (UIntPtr)(codeRva + StubCodeBytes), 0x3000, 0x04);
        IntPtr window = VirtualAlloc(IntPtr.Zero, (UIntPtr)0x4000, 0x3000, 0x04);
        try
        {
            if (qt == IntPtr.Zero || fixture == IntPtr.Zero || window == IntPtr.Zero)
                throw new Exception("Cannot allocate the repeat logging fixture");
            CopyImageCode(image, fixture, codeRva, Marshal.ReadInt32(image, codeRva + 14 * 4));
            CopyImageCode(image, fixture, firstGate, reject - firstGate);
            CopyImageCode(image, fixture, secondGate, signoff - secondGate);

            // Set up only the guiUpdate registers and stack slots used by the two
            // recent-call comparisons. The two exits return to the harness.
            const int entry = 0x1000;
            byte[] setup = {
                0x53, 0x41, 0x55,                        // save rbx and r13
                0x48, 0x81, 0xec, 0xa8, 1, 0, 0,         // sub rsp, 0x1a8
                0x48, 0x89, 0xcb,                        // mov rbx, rcx
                0x48, 0x89, 0x94, 0x24, 0xe8, 0, 0, 0,   // save QString::operator== at rsp+0xe8
                0x48, 0x8b, 0x83, 0x60, 0x2a, 0, 0,      // load active callsign
                0x48, 0x89, 0x84, 0x24, 0x58, 1, 0, 0    // save QString at rsp+0x158
            };
            WriteCode(fixture, entry, setup);
            WriteJump(fixture, entry + setup.Length, firstGate);
            byte[] finish = { 0xb8, 0, 0, 0, 0, 0x48, 0x81, 0xc4, 0xa8, 1, 0, 0, 0x41, 0x5d, 0x5b, 0xc3 };
            WriteCode(fixture, reject, finish);
            finish[1] = 1;
            WriteCode(fixture, signoff, finish);

            // Return the fixture's history time and status. Model each QDateTime
            // by its epoch value, which is what the native Qt conversion returns.
            WriteCode(fixture, 0x12be80, new byte[] { 0x8b, 0x01, 0x41, 0x89, 0x00, 0x8b, 0x41, 0x04, 0xc3 });
            WriteCode(fixture, 0x2d91f0, new byte[] { 0xc3 });
            WriteCode(fixture, 0x1100, new byte[] { 0x48, 0x8b, 0x01, 0xc3 });
            Marshal.WriteIntPtr(fixture, 0x2c1e378, IntPtr.Add(fixture, 0x1100));
            Marshal.WriteIntPtr(fixture, 0x2c1c8c0, Find(qt, "_ZN10QArrayData11shared_nullE"));
            uint prior;
            if (!VirtualProtect(fixture, (UIntPtr)(codeRva + StubCodeBytes), 0x20, out prior))
                throw new Exception("Cannot protect the repeat logging fixture");

            IntPtr dx = IntPtr.Add(window, 0x3000), other = IntPtr.Add(window, 0x3080), mode = IntPtr.Add(window, 0x3100);
            WriteQtFixtureString(dx, "W1AW");
            WriteQtFixtureString(other, "W2ABC");
            WriteQtFixtureString(mode, "FT8");
            IntPtr call = Marshal.ReadIntPtr(dx), different = Marshal.ReadIntPtr(other);
            Marshal.WriteIntPtr(window, 0x2a60, call);
            Marshal.WriteIntPtr(window, 0x2ae8, Marshal.ReadIntPtr(mode));
            Marshal.WriteIntPtr(window, 0x6c0, call);
            Marshal.WriteIntPtr(window, 0x6c8, different);
            Marshal.WriteInt32(window, history + 4, 10);
            Marshal.WriteInt64(window, lastLogged, utcMidnight + 29800000);
            Marshal.WriteInt64(window, exchangeStart, utcMidnight + 29765000);
            Marshal.WriteInt32(window, history, 29765);
            RecentCallLogGate gate = Function<RecentCallLogGate>(IntPtr.Add(fixture, entry));
            IntPtr equals = Find(qt, "_ZeqRK7QStringS1_");
            Check(gate(window, equals) == 0, "A repeated sign-off in the same exchange is excluded from logging");

            Marshal.WriteIntPtr(window, 0x6c0, different);
            Marshal.WriteIntPtr(window, 0x6c8, call);
            Marshal.WriteInt64(window, lastLogged, utcMidnight + 29915000);
            Marshal.WriteInt64(window, exchangeStart, utcMidnight + 30155000);
            Marshal.WriteInt32(window, history, 30155);
            Check(gate(window, equals) == 1, "A new exchange reaches logging when its call is the second most recent");

            Marshal.WriteIntPtr(window, 0x6c0, call);
            Marshal.WriteIntPtr(window, 0x6c8, different);
            Check(gate(window, equals) == 1, "A new exchange reaches logging with the immediately previous call");
            Marshal.WriteInt64(window, lastLogged, utcMidnight + 30185000);
            Check(gate(window, equals) == 0, "After logging the new exchange, retransmission cannot log it again");

            Marshal.WriteInt64(window, lastLogged, utcMidnight - 5000);
            Marshal.WriteInt64(window, exchangeStart, utcMidnight + 7500);
            Marshal.WriteInt32(window, history, 7);
            Check(gate(window, equals) == 0, "A timestamp from a different exchange cannot reopen a recently logged call");
            Marshal.WriteInt64(window, exchangeStart, utcMidnight + 7000);
            WriteQtFixtureString(mode, "FT4");
            Check(gate(window, equals) == 1, "FT4 repeats can log across UTC midnight");
            Marshal.WriteByte(window, 0x68b, 1);
            Check(gate(window, equals) == 0, "DXpedition mode keeps the original recent-call logging guard");
            Marshal.WriteByte(window, 0x68b, 0);
            Marshal.WriteInt32(window, history + 4, 0);
            Check(gate(window, equals) == 0, "A recent call without an exchange in JTDX history is still excluded");

            Check(Marshal.ReadByte(image, 0x10f5b6) == 0xe9 &&
                0x10f5bb + Marshal.ReadInt32(image, 0x10f5b7) == 0x10f5f2,
                "The ordinary log timer accepts a new exchange that passes the recent-call guard");
        }
        finally
        {
            if (window != IntPtr.Zero) VirtualFree(window, UIntPtr.Zero, 0x8000);
            if (fixture != IntPtr.Zero) VirtualFree(fixture, UIntPtr.Zero, 0x8000);
            if (qt != IntPtr.Zero) FreeLibrary(qt);
        }
    }

    static void TestSentReportLogging(IntPtr image, string dllDirectory)
    {
        const int codeRva = 0x2d17000, stateRva = codeRva + StubCodeBytes;
        const int entry = 0x1000, logCall = 0x0e8edc, logReturn = 0x0e8ee1;
        const int reportEntry = 0x1100, rogerEntry = 0x1140, reportReturn = 0x10e873;
        const int reportSite = 0x110754, rogerSite = 0x1104d0;
        const int logOn73 = 0x65f, hound = 0x68b;
        IntPtr qt = LoadLibraryEx(Path.Combine(dllDirectory, "Qt5Core.dll"), IntPtr.Zero, 8);
        IntPtr kernel = LoadLibraryEx(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), IntPtr.Zero, 0);
        IntPtr fixture = VirtualAlloc(IntPtr.Zero, (UIntPtr)(stateRva + 0x1000), 0x3000, 0x04);
        IntPtr window = VirtualAlloc(IntPtr.Zero, (UIntPtr)0x4000, 0x3000, 0x04);
        try
        {
            if (qt == IntPtr.Zero || kernel == IntPtr.Zero || fixture == IntPtr.Zero || window == IntPtr.Zero)
                throw new Exception("Cannot allocate the sent report fixture");
            CopyImageCode(image, fixture, codeRva, Marshal.ReadInt32(image, codeRva + 14 * 4));
            CopyImageCode(image, fixture, logCall, 5);
            CopyImageCode(image, fixture, reportSite, 5);
            CopyImageCode(image, fixture, rogerSite, 5);
            byte[] guiFrame = {
                0x53,                              // push rbx
                0x48, 0x83, 0xec, 0x20,            // sub rsp, 0x20
                0x48, 0x89, 0xcb                   // mov rbx, rcx
            };
            WriteCode(fixture, reportEntry, guiFrame);
            WriteJump(fixture, reportEntry + guiFrame.Length, reportSite);
            WriteCode(fixture, rogerEntry, guiFrame);
            WriteJump(fixture, rogerEntry + guiFrame.Length, rogerSite);
            WriteCode(fixture, reportReturn, new byte[] { 0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3 });
            byte[] frame = {
                0x41, 0x56, 0x57,                   // push r14; push rdi
                0x48, 0x81, 0xec, 0xc8, 0, 0, 0,   // sub rsp, 0xc8
                0x49, 0x89, 0xce,                   // mov r14, rcx
                0x48, 0x89, 0xd7,                   // mov rdi, rdx
                0x48, 0x89, 0x7c, 0x24, 0x20,       // original fifth argument
                0x4c, 0x89, 0x4c, 0x24, 0x28,       // save the fixture's time output pointer
                0x41, 0x8b, 0x01,                   // load initial history seconds
                0x89, 0x84, 0x24, 0xbc, 0, 0, 0    // mov [rsp+0xbc], eax
            };
            WriteCode(fixture, entry, frame);
            WriteJump(fixture, entry + frame.Length, logCall);
            WriteCode(fixture, logReturn, new byte[] {
                0x48, 0x8b, 0x54, 0x24, 0x28,       // get the time output pointer
                0x8b, 0x8c, 0x24, 0xbc, 0, 0, 0,   // get the seconds used by the native logger
                0x89, 0x0a,
                0x48, 0x81, 0xc4, 0xc8, 0, 0, 0, 0x5f, 0x41, 0x5e, 0xc3
            });
            WriteCode(fixture, 0x12be80, new byte[] {
                0x48, 0x39, 0x7c, 0x24, 0x28,       // the original call must receive rdi on the stack
                0x75, 0x04,
                0x44, 0x89, 0xc0, 0xc3,             // return the supplied history status
                0xb8, 0xff, 0xff, 0xff, 0xff, 0xc3
            });
            // Use the real Qt string conversion and destructor. Substitute
            // only the three-character report's strlen and the radio clock.
            CopyImageCode(image, fixture, 0x2d91b0, 0x35);
            CopyImageCode(image, fixture, 0x2d91f0, 0x35);
            WriteCode(fixture, 0x081a88, new byte[] { 0x31, 0xc0, 0x80, 0x3c, 0x01, 0, 0x74, 0x05, 0x48, 0xff, 0xc0, 0xeb, 0xf5, 0xc3 });
            WriteCode(fixture, 0x1595b0, new byte[] { 0x48, 0x8b, 0x01, 0xc3 });
            Marshal.WriteIntPtr(fixture, 0x2c1d3f8, Find(qt, "_ZN7QString16fromAscii_helperEPKci"));
            Marshal.WriteIntPtr(fixture, 0x2c1c8b8, Find(qt, "_ZN10QArrayData10deallocateEPS_yy"));
            Marshal.WriteIntPtr(fixture, stateRva + 8, Find(kernel, "GetTickCount64"));
            IntPtr assign = Find(qt, "_ZN7QStringaSERKS_");
            Marshal.WriteIntPtr(fixture, 0x2c1d528, assign);
            uint prior;
            if (!VirtualProtect(fixture, (UIntPtr)stateRva, 0x20, out prior))
                throw new Exception("Cannot protect the sent report fixture");

            IntPtr history = IntPtr.Add(window, 0x3000), saved = IntPtr.Add(window, 0x3080);
            IntPtr sent = IntPtr.Add(window, 0x3100), lastTx = IntPtr.Add(window, 0x3180);
            IntPtr dx = IntPtr.Add(window, 0x3200), mode = IntPtr.Add(window, 0x3280);
            IntPtr clock = IntPtr.Add(window, 0x3300), seconds = IntPtr.Add(window, 0x3308);
            const long utcMidnight = 20000L * 86400000;
            const long fortyMetres = 7074000L;
            Action<IntPtr, string> staticQString = (target, value) => {
                WriteQtFixtureString(target, value);
                Marshal.WriteInt32(target, 8, -1); // static reference count for Qt's copy assignment
            };
            staticQString(history, "-17");
            staticQString(dx, "W2ABC");
            staticQString(mode, "FT8");
            staticQString(sent, "-11");
            staticQString(lastTx, "");
            Marshal.WriteIntPtr(saved, Marshal.ReadIntPtr(history));
            Marshal.WriteIntPtr(window, 0x38, clock);
            Marshal.WriteInt64(window, 0x340, fortyMetres);
            Marshal.WriteIntPtr(window, 0x628, Marshal.ReadIntPtr(lastTx));
            Marshal.WriteIntPtr(window, 0x640, Marshal.ReadIntPtr(lastTx));
            Marshal.WriteIntPtr(window, 0x2a60, Marshal.ReadIntPtr(dx));
            Marshal.WriteIntPtr(window, 0x2ae8, Marshal.ReadIntPtr(mode));
            Marshal.WriteIntPtr(window, 0x2b10, Marshal.ReadIntPtr(sent));
            Marshal.WriteByte(window, logOn73, 1);
            Marshal.WriteByte(fixture, stateRva + 16, 2);
            Marshal.WriteInt64(fixture, stateRva, -1);
            UiAction remember = Function<UiAction>(IntPtr.Add(fixture, reportEntry));
            UiAction rememberRoger = Function<UiAction>(IntPtr.Add(fixture, rogerEntry));
            UiText reset = Function<UiText>(assign);
            ReportLog log = Function<ReportLog>(IntPtr.Add(fixture, entry));
            Action<string, string, long, bool> transmitted = (call, report, ms, roger) => {
                staticQString(dx, call);
                staticQString(sent, report);
                staticQString(lastTx, call + " K1ABC " + (roger ? "R" : "") + report);
                Marshal.WriteInt64(clock, utcMidnight + ms);
                if (roger) rememberRoger(window);
                else remember(window);
            };
            Func<string, long, int, int, Tuple<string, int>> recorded = (call, ms, start, status) => {
                staticQString(dx, call);
                Marshal.WriteInt64(clock, utcMidnight + ms);
                Marshal.WriteInt32(seconds, start);
                reset(saved, history);
                if (log(window, saved, status, seconds) != status) throw new Exception("Logging changed the history status");
                return Tuple.Create(QtString(saved), Marshal.ReadInt32(seconds));
            };
            transmitted("W2ABC", "-11", 30042, false);
            Check(recorded("W2ABC", 60078, 15, 10).Equals(Tuple.Create("-11", 15)),
                "FT8 uses the transmitted report and keeps a normal QSO start");
            transmitted("W2ABC", "-08", 90078, false);
            transmitted("W2ABC", "-09", 120123, false);
            transmitted("DL1ZZZ", "-16", 150120, false);
            Check(recorded("W2ABC", 240100, 0, 10).Equals(Tuple.Create("-09", 90)),
                "Late response after another station restores the last sent report and the first actual start");
            Check(recorded("W2ABC", 240150, 0, 10).Equals(Tuple.Create("-17", 0)),
                "A completed exchange cannot supply the report for a later QSO");
            transmitted("DL1ZZZ", "-13", 300500, false);
            transmitted("W2ABC", "-22", 330100, false);
            Check(recorded("DL1ZZZ", 390005, 360, 10).Equals(Tuple.Create("-13", 300)),
                "An intervening exchange timestamp cannot replace the time of the transmitted report");
            transmitted("W3XYZ", "-12", 420010, false);
            transmitted("W4XYZ", "-10", 450010, false);
            transmitted("W3XYZ", "-06", 480080, false);
            transmitted("W4XYZ", "-11", 510135, false);
            Check(recorded("W3XYZ", 570120, 0, 10).Equals(Tuple.Create("-06", 480)),
                "A new attempt starts at the new report after another station");
            staticQString(mode, "FT4");
            transmitted("W2ABC", "+00", 86400000 - 7500, true);
            Check(recorded("W2ABC", 86400000 + 7510, 0, 10).Equals(Tuple.Create("+00", 86392)),
                "FT4 carries the transmitted R+00 and its start across UTC midnight");

            staticQString(mode, "FT8");
            staticQString(dx, "W2ABC");
            staticQString(sent, "-11");
            staticQString(lastTx, "W2ABCD K1ABC -11");
            remember(window);
            Tuple<string, int> native = Tuple.Create("-17", 0);
            Check(recorded("W2ABC", 86415000, 0, 10).Equals(native),
                "An unrelated recipient with the same callsign prefix does not supply a report");

            transmitted("W2ABC", "-11", 86430000, false);
            bool early = recorded("W2ABC", 86460000, 0, 7).Equals(native);
            Marshal.WriteByte(window, logOn73, 0);
            bool manual = recorded("W2ABC", 86460000, 0, 10).Equals(native);
            Marshal.WriteByte(window, logOn73, 1);
            Marshal.WriteByte(window, hound, 1);
            bool dxpedition = recorded("W2ABC", 86460000, 0, 10).Equals(native);
            Marshal.WriteByte(window, hound, 0);
            staticQString(mode, "FT4");
            bool anotherMode = recorded("W2ABC", 86460000, 0, 10).Equals(native);
            staticQString(mode, "FT8");
            Marshal.WriteInt64(window, 0x340, 14074000L);
            bool anotherBand = recorded("W2ABC", 86460000, 0, 10).Equals(native);
            Marshal.WriteInt64(window, 0x340, fortyMetres);
            bool stale = recorded("W2ABC", 86731000, 0, 10).Equals(native);
            Check(early && manual && dxpedition && anotherMode && anotherBand && stale,
                "Manual logging, an incomplete exchange, other modes, bands and stale reports preserve native logging");

            transmitted("W2ABC", "-11", 86760000, false);
            Marshal.WriteInt64(fixture, stateRva, 0);
            bool expired = recorded("W2ABC", 86790000, 0, 10).Equals(native);
            Control control = Function<Control>(IntPtr.Add(fixture, codeRva + Marshal.ReadInt32(image, codeRva + 12 * 4)));
            Send(control, JtdxAutoCq.QtPacket.HoldNativeSelection("JTDX", 3, false, true));
            Check(expired && recorded("W2ABC", 86790000, 0, 10).Equals(native),
                "An expired lease and a newly armed session cannot reuse the previous exchange");
            reset(saved, history);
        }
        finally
        {
            if (window != IntPtr.Zero) VirtualFree(window, UIntPtr.Zero, 0x8000);
            if (fixture != IntPtr.Zero) VirtualFree(fixture, UIntPtr.Zero, 0x8000);
            if (kernel != IntPtr.Zero) FreeLibrary(kernel);
            if (qt != IntPtr.Zero) FreeLibrary(qt);
        }
    }

    static void TestHistoryUpdates(IntPtr image, string dllDirectory)
    {
        const int codeRva = 0x2d17000, stateRva = codeRva + StubCodeBytes, pageSize = 0x1000;
        const int prepare = 0x12a1c1, reject = 0x12a26e, accept = 0x12a410, reportException = 0x12b3c0;
        const int entry = 0x1000, historyData = 0x2000, radioClock = 0x1595b0;
        const int call = 4, report = 6, sentReport = 7, rogerReport = 8, finished = 16;
        const int reopened = 3;
        const long midnight = 20000L * 86400000;
        IntPtr qt = LoadLibraryEx(Path.Combine(dllDirectory, "Qt5Core.dll"), IntPtr.Zero, 8);
        if (qt == IntPtr.Zero) throw new Exception("Cannot load Qt for the history fixture");
        IntPtr fixture = IntPtr.Zero;
        try
        {
            fixture = VirtualAlloc(IntPtr.Zero, (UIntPtr)(stateRva + pageSize), 0x3000, 0x04);
            if (fixture == IntPtr.Zero) throw new Exception("Cannot allocate the history comparison fixture");
            CopyImageCode(image, fixture, codeRva, Marshal.ReadInt32(image, codeRva + 14 * 4));
            CopyImageCode(image, fixture, prepare, reject - prepare);
            byte[] frame = {
                0x53, 0x56, 0x41, 0x55, 0x41, 0x56,       // save rbx, rsi, r13 and r14
                0x48, 0x81, 0xec, 0x68, 0x04, 0, 0,       // provide the history and display frames
                0x41, 0x89, 0xcd,                         // r13d = incoming time
                0x44, 0x89, 0xc6,                         // esi = incoming status
                0x89, 0x94, 0x24, 0x70, 0x01, 0, 0,       // [rsp+170h] = saved time
                0x44, 0x89, 0x8c, 0x24, 0x58, 0x01, 0, 0, // [rsp+158h] = saved status
                0xc7, 0x84, 0x24, 0x64, 0x01, 0, 0, 15, 0, 0, 0, // saved TX was 73
                0x48, 0x8b, 0x9c, 0x24, 0xb0, 0x04, 0, 0, // rbx = fifth argument (QsoHistory fixture)
                0x4c, 0x8b, 0x73, 0x18,                   // r14 = mode QString
                0x48, 0x8b, 0x43, 0x10,
                0x48, 0x89, 0x84, 0x24, 0x58, 0x02, 0, 0, // display's return address
                0x48, 0x8b, 0x84, 0x24, 0xb8, 0x04, 0, 0,
                0x48, 0x89, 0x84, 0x24, 0x50, 0x04, 0, 0, // display's current DX
                0x48, 0x8b, 0x84, 0x24, 0xc0, 0x04, 0, 0,
                0x48, 0x8b, 0x00,
                0x48, 0x89, 0x84, 0x24, 0x00, 0x01, 0, 0,
                0x48, 0x89, 0x84, 0x24, 0x08, 0x01, 0, 0  // the report's normalized call QString and original copy
            };
            WriteCode(fixture, entry, frame);
            WriteJump(fixture, entry + frame.Length, prepare);
            byte[] result = { 0xb8, 0, 0, 0, 0, 0x48, 0x81, 0xc4, 0x68, 0x04, 0, 0, 0x41, 0x5e, 0x41, 0x5d, 0x5e, 0x5b, 0xc3 };
            WriteCode(fixture, reject, result);
            byte[] accepted = {
                0x31, 0xc0,                          // an ordinary accepted update returns 1
                0x83, 0xfe, 0x06,                    // only the plain report can reopen a FIN
                0x75, 0x17,
                0x83, 0xbc, 0x24, 0x58, 0x01, 0, 0, 0,
                0x75, 0x0d,
                0x83, 0xbc, 0x24, 0x64, 0x01, 0, 0, 0,
                0x75, 0x03,
                0x83, 0xc0, 0x02                     // a reset also clears the old transmitted 73
            };
            WriteCode(fixture, accept, accepted);
            result[1] = 1; result[0] = 0x05; // add eax, 1
            WriteCode(fixture, accept + accepted.Length, result);
            result[0] = 0xb8;
            result[1] = 2; WriteCode(fixture, reportException, result);
            WriteCode(fixture, radioClock, new byte[] { 0x48, 0x8b, 0x01, 0xc3 }); // return the simulated clock value
            Marshal.WriteIntPtr(fixture, 0x2c1e510, Find(qt, "_ZeqRK7QStringS1_"));
            WriteCode(fixture, 0x1100, new byte[] { 0xb8, 0xe8, 0x03, 0, 0, 0xc3 }); // monotonic tick = 1000
            Marshal.WriteIntPtr(fixture, stateRva + 8, IntPtr.Add(fixture, 0x1100));
            uint prior;
            foreach (int page in new[] { entry, 0x12a000, 0x12b000, 0x159000, codeRva, codeRva + pageSize })
                if (!VirtualProtect(IntPtr.Add(fixture, page), (UIntPtr)pageSize, 0x20, out prior))
                    throw new Exception("Cannot protect the history comparison fixture");

            IntPtr history = IntPtr.Add(fixture, historyData);
            IntPtr clock = IntPtr.Add(history, 8);
            Marshal.WriteIntPtr(history, clock);
            Marshal.WriteIntPtr(history, 16, IntPtr.Add(fixture, 0x13bac1));
            IntPtr peer = IntPtr.Add(history, 0x40), sameCall = IntPtr.Add(history, 0x80), anotherCall = IntPtr.Add(history, 0xc0);
            IntPtr mode = IntPtr.Add(history, 0x100);
            WriteQtFixtureString(peer, "W2ABC");
            WriteQtFixtureString(sameCall, "W2ABC");
            WriteQtFixtureString(anotherCall, "W2ABCD");
            WriteQtFixtureString(mode, "FT8");
            Marshal.WriteIntPtr(history, 24, mode);
            Marshal.WriteInt32(peer, 8, -1); // static QString reference for the native cleanup paths
            HistoryUpdate nativeUpdate = Function<HistoryUpdate>(IntPtr.Add(fixture, entry));
            Func<int, int, int, int, int> update = (incoming, saved, status, previous) =>
                nativeUpdate(incoming, saved, status, previous, history, anotherCall, peer);
            Marshal.WriteInt64(clock, midnight + 1320000);
            Check(update(1305, 84090, call, finished) == 1,
                "A fresh addressed call after UTC midnight replaces the previous day's FIN");
            Check(update(1305, 21600, call, finished) == 1,
                "The UTC rollover also works with a saved QSO from earlier on the previous day");
            Check(update(1320, 1305, sentReport, call) == 1
                && update(1335, 1320, rogerReport, sentReport) == 1,
                "The new exchange accepts both the outgoing report and the R-report for RR73");

            Marshal.WriteInt64(clock, midnight + 14500);
            Check(update(0, 86385, rogerReport, sentReport) == 1,
                "An active QSO accepts its next report at exactly midnight");
            Check(update(86370, 86385, call, finished) == 0,
                "An older decode from before midnight cannot replace a later history entry from that day");

            Marshal.WriteInt64(clock, midnight + 3829000);
            Check(update(3800, 3815, call, finished) == 0
                && update(3800, 3815, report, finished) == 2
                && update(3800, 86400, call, finished) == 0,
                "Same-day ordering, the received-report exception and invalid time handling are preserved");
            Marshal.WriteIntPtr(history, IntPtr.Zero);
            Check(update(1305, 84090, call, finished) == 0,
                "An unavailable radio clock leaves the native ordering in effect");

            Marshal.WriteIntPtr(history, clock);
            Marshal.WriteInt64(clock, midnight + 42614200);
            Marshal.WriteInt64(fixture, stateRva, -1);
            Marshal.WriteByte(fixture, stateRva + 16, 2);
            Check(update(42600, 36765, report, finished) == reopened,
                "A new plain report can start a repeat QSO and replaces the completed exchange");
            Marshal.WriteByte(fixture, stateRva + 16, 10);
            Check(nativeUpdate(42600, 42585, report, finished, history, sameCall, peer) == 1,
                "A repeated report from the current peer preserves the active final 73");
            Check(update(42600, 42585, report, finished) == reopened,
                "An incoming caller with a similar callsign can start a new exchange during another sign-off");
            Check(update(42600, 42600, report, finished) == 1
                && update(42600, 42605, report, finished) == 2
                && update(42570, 36765, report, finished) == 1
                && update(42600, 36765, rogerReport, finished) == 1,
                "Old, duplicate and acknowledged reports do not reopen a finished QSO");
            Marshal.WriteInt64(clock, midnight + 14600);
            Check(update(0, 86385, report, finished) == reopened,
                "An addressed plain report can reopen a previous day's finished exchange at UTC midnight");
            WriteQtFixtureString(mode, "FT4");
            Marshal.WriteInt64(clock, midnight + 1319900);
            Check(update(1312, 1275, report, finished) == reopened,
                "A repeat FT4 exchange also starts with a fresh plain report");
            WriteQtFixtureString(mode, "JT9");
            Check(update(1305, 1275, report, finished) == 1,
                "A report in another mode keeps the original history behavior");
            WriteQtFixtureString(mode, "FT8");
            Marshal.WriteInt64(fixture, stateRva, 0);
            Check(update(1305, 1275, report, finished) == 1,
                "After the control lease expires, JTDX handles a report using its original history");
        }
        finally
        {
            if (fixture != IntPtr.Zero) VirtualFree(fixture, UIntPtr.Zero, 0x8000);
            FreeLibrary(qt);
        }
    }

    static void TestPeMapping(string path, string dllDirectory)
    {
        const uint noImportsOrEntryPoint = 1;
        IntPtr image = LoadLibraryEx(path, IntPtr.Zero, noImportsOrEntryPoint);
        if (image == IntPtr.Zero) throw new Exception("Windows rejected the prepared PE: " + Marshal.GetLastWin32Error());
        try
        {
            ulong imageBase;
            ulong pc = unchecked((ulong)image.ToInt64()) + 0x2d17000UL;
            int firstEntry = Marshal.ReadInt32(IntPtr.Add(image, 0x2d17000));
            IntPtr unwind = RtlLookupFunctionEntry(pc + (uint)firstEntry, out imageBase, IntPtr.Zero);
            Check(unwind != IntPtr.Zero && imageBase == unchecked((ulong)image.ToInt64()),
                "Windows maps the prepared executable and recognizes the new handler's unwind table");
            Check(Marshal.ReadByte(image, 0x3078f9) == 0xb8 && Marshal.ReadInt32(image, 0x3078fa) == 1
                && Marshal.ReadByte(image, 0x307974) == 0xb8 && Marshal.ReadInt32(image, 0x307975) == 1
                && Marshal.ReadByte(image, 0x0aa30f) == 0xe8
                && 0x0aa314 + Marshal.ReadInt32(image, 0x0aa310) == 0x31b220,
                "Both TriggerCQ paths bypass the prompt check while normal QSO completion still reads the setting");
            TestSignoffLogging(image, dllDirectory);
            TestRepeatLogging(image, dllDirectory);
            TestSentReportLogging(image, dllDirectory);
            TestHistoryUpdates(image, dllDirectory);
            foreach (int index in new[] { 43, 45 })
            {
                int historyEntry = Marshal.ReadInt32(image, 0x2d17000 + index * 4);
                unwind = RtlLookupFunctionEntry(pc + (uint)historyEntry, out imageBase, IntPtr.Zero);
                Check(unwind != IntPtr.Zero && imageBase == unchecked((ulong)image.ToInt64()),
                    "Windows recognizes the QSO history hook's unwind table");
            }
            int textEntry = Marshal.ReadInt32(image, 0x2d17000 + 48 * 4);
            Check(Marshal.ReadByte(image, 0x10eb2b) == 0xe9
                && 0x10eb30 + Marshal.ReadInt32(image, 0x10eb2c) == 0x2d17000 + textEntry,
                "The actual text dispatch in the prepared JTDX is guarded as well as the TX stage");
            unwind = RtlLookupFunctionEntry(pc + (uint)textEntry, out imageBase, IntPtr.Zero);
            Check(unwind != IntPtr.Zero && imageBase == unchecked((ulong)image.ToInt64()),
                "Windows recognizes the final sign-off text hook's unwind table");
        }
        finally { FreeLibrary(image); }
    }

    static void TestReplyGate(byte[] code)
    {
        const int codeRva = 0x2d17000;
        const int entry = 0x1000;
        const int fixtureSize = codeRva + StubCodeBytes;
        int runReply = codeRva + BitConverter.ToInt32(code, 22 * 4);
        int gate = codeRva + BitConverter.ToInt32(code, 23 * 4);
        IntPtr image = VirtualAlloc(IntPtr.Zero, (UIntPtr)fixtureSize, 0x3000, 0x04);
        if (image == IntPtr.Zero) throw new Exception("Cannot allocate the isolated Reply fixture");
        try
        {
            WriteCode(image, codeRva, code);
            // Establish the original replyToUDP alignment, marker argument,
            // and nonvolatile register that supplies the stand-in this pointer.
            WriteCode(image, entry, new byte[] {
                0x53,                               // push rbx
                0x48, 0x81, 0xec, 0xa0, 2, 0, 0,    // sub rsp, 0x2a0
                0x88, 0x8c, 0x24, 0x90, 2, 0, 0,    // mov [rsp+0x290], cl
                0x89, 0xd3                          // mov ebx, edx
            });
            WriteJump(image, entry + 17, runReply);

            // Simulate only the frame setup and epilogue of processMessage
            // and substitute the supplied setting for Configuration.
            WriteCode(image, 0x0c3320, new byte[] { 0x48, 0x81, 0xec, 0x48, 5, 0, 0 });
            WriteJump(image, 0x0c3327, gate);
            WriteCode(image, 0x31b220, new byte[] { 0x89, 0xc8, 0xc3 }); // mov eax, ecx; ret
            WriteCode(image, 0x0c4fd7, new byte[] { 0x48, 0x81, 0xc4, 0x48, 5, 0, 0, 0xc3 });
            WriteCode(image, 0x09f866, new byte[] { 0x48, 0x81, 0xc4, 0xa0, 2, 0, 0, 0x5b, 0xc3 });
            uint prior;
            if (!VirtualProtect(image, (UIntPtr)fixtureSize, 0x20, out prior)) throw new Exception("Cannot protect the Reply fixture");
            ReplyGate check = Function<ReplyGate>(IntPtr.Add(image, entry));
            Check(check(0, 0) == 0 && check(0, 1) == 1 && check(0xff, 0) == 0
                && check(0xa8, 0) == 1 && check(0xa9, 0) == 1
                && check(0, 0) == 0,
                "Assembled Reply path enables TX with PromptToLog off only for AutoCQ caller and visible CQ requests");
        }
        finally { VirtualFree(image, UIntPtr.Zero, 0x8000); }
    }

    static void WriteCode(IntPtr image, int rva, byte[] code)
    { Marshal.Copy(code, 0, IntPtr.Add(image, rva), code.Length); }

    static void WriteJump(IntPtr image, int from, int to)
    {
        Marshal.WriteByte(image, from, 0xe9);
        Marshal.WriteInt32(image, from + 1, to - from - 5);
    }

    static void WriteQtFixtureString(IntPtr obj, string text)
    {
        IntPtr data = IntPtr.Add(obj, 8);
        Marshal.WriteIntPtr(obj, data);
        Marshal.WriteInt32(data, 4, text.Length);
        Marshal.WriteInt64(data, 16, 24);
        byte[] utf16 = Encoding.Unicode.GetBytes(text);
        Marshal.Copy(utf16, 0, IntPtr.Add(data, 24), utf16.Length);
    }

    static void TestDirectCall(byte[] code)
    {
        const int codeRva = 0x2d17000;
        const int entry = 0x1000;
        const int fixtureSize = codeRva + StubCodeBytes;
        int hook = codeRva + BitConverter.ToInt32(code, 32 * 4);
        IntPtr image = VirtualAlloc(IntPtr.Zero, (UIntPtr)fixtureSize, 0x3000, 0x04);
        IntPtr window = VirtualAlloc(IntPtr.Zero, (UIntPtr)0x4000, 0x3000, 0x04);
        if (image == IntPtr.Zero || window == IntPtr.Zero) throw new Exception("Cannot allocate the direct-call fixture");
        var actions = new List<string>();
        var callbacks = new List<Delegate>();
        try
        {
            WriteCode(image, codeRva, code);
            byte[] frame = {
                0x53, 0x48, 0x81, 0xec, 0xa0, 2, 0, 0,   // push rbx; sub rsp, 0x2a0
                0x48, 0x89, 0xcb,                         // mov rbx, rcx
                0x48, 0x89, 0x94, 0x24, 0x80, 2, 0, 0,   // mov [rsp+0x280], rdx
                0x44, 0x88, 0x84, 0x24, 0x90, 2, 0, 0    // mov [rsp+0x290], r8b
            };
            WriteCode(image, entry, frame);
            WriteJump(image, entry + frame.Length, hook);
            byte[] finish = { 0x48, 0x81, 0xc4, 0xa0, 2, 0, 0, 0x5b, 0xc3 };
            WriteCode(image, 0x09ef68, finish);
            WriteCode(image, 0x09ef93, finish);
            Action<int, Delegate> imported = (rva, callback) =>
            {
                callbacks.Add(callback);
                Marshal.WriteIntPtr(image, rva, Marshal.GetFunctionPointerForDelegate(callback));
            };
            Action<int, UiAction> native = (rva, callback) =>
            {
                callbacks.Add(callback);
                WriteCode(image, rva, new byte[] { 0x48, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xe0 });
                Marshal.WriteIntPtr(image, rva + 2, Marshal.GetFunctionPointerForDelegate(callback));
            };
            IntPtr ui = IntPtr.Add(window, 0x3000);
            IntPtr mode = IntPtr.Add(window, 0x3800);
            IntPtr call = IntPtr.Add(window, 0x3860);
            IntPtr tx1 = IntPtr.Add(window, 0x38c0);
            Marshal.WriteIntPtr(window, 0x90, ui);
            Marshal.WriteIntPtr(window, 0x2ae8, IntPtr.Add(mode, 8));
            foreach (int widget in new[] { 0x7a8, 0x7b0, 0x9d0, 0xa30, 0xa98, 0xbf0, 0xbf8 })
                Marshal.WriteIntPtr(ui, widget, (IntPtr)widget);
            WriteQtFixtureString(mode, "FT8");
            WriteQtFixtureString(call, "W3ABC");
            WriteQtFixtureString(tx1, "W3ABC K1ABC FN20");
            int tab = 0;
            bool enabled = false;
            imported(0x2c20670, new UiAction(widget => actions.Add("clear-grid/" + widget.ToInt32().ToString("x"))));
            imported(0x2c20680, new UiText((widget, value) => actions.Add(widget.ToInt32().ToString("x") + "/" + QtString(value))));
            imported(0x2c208f8, new UiValue(widget => tab));
            imported(0x2c20c78, new UiText((output, widget) => Marshal.WriteIntPtr(output, Marshal.ReadIntPtr(tx1))));
            imported(0x2c1fc40, new UiSetValue((widget, on) => actions.Add("check/" + widget.ToInt32().ToString("x") + "/" + on)));
            imported(0x2c209e8, new UiValue(widget => enabled ? 1 : 0));
            imported(0x2c1fc70, new UiAction(widget => actions.Add("click/" + widget.ToInt32().ToString("x"))));
            native(0x0f9290, receiver => actions.Add("generate"));
            native(0x0d08d0, receiver => actions.Add("tx1"));
            native(0x2d91f0, receiver => { });
            native(0x316690, receiver => actions.Add("status"));
            uint prior;
            if (!VirtualProtect(image, (UIntPtr)fixtureSize, 0x20, out prior)) throw new Exception("Cannot protect direct-call code");
            var receive = Function<DirectCall>(IntPtr.Add(image, entry));
            foreach (int marker in new[] { 0, 0xa8, 0xa9 }) receive(window, call, marker);
            Check(actions.Count == 0, "Normal and decoded Reply markers do not select a direct callsign");
            foreach (int flag in new[] { 0x2e44, 0x2e47, 0x5f8 })
            {
                Marshal.WriteByte(window, flag, 1);
                receive(window, call, 0xaa);
                Marshal.WriteByte(window, flag, 0);
            }
            WriteQtFixtureString(mode, "JT9");
            receive(window, call, 0xaa);
            Check(actions.Count == 0, "Direct calls preserve an active transmission, watchdog, tune and other modes");
            WriteQtFixtureString(mode, "FT8");
            receive(window, call, 0xaa);
            Check(String.Join(",", actions) == "clear-grid/7a8,7b0/W3ABC,generate,tx1,click/9d0",
                "FT8 direct call selects Tx1 and enables TX without reading a decode");
            actions.Clear();
            tab = 1; enabled = true;
            WriteQtFixtureString(mode, "FT4");
            receive(window, call, 0xaa);
            Check(String.Join(",", actions) == "clear-grid/7a8,7b0/W3ABC,generate,tx1,bf0/W3ABC K1ABC FN20,check/bf8/1,status"
                && Marshal.ReadInt32(window, 0x4f4) == 7,
                "FT4 direct call also selects the generated message in TX tab two and leaves enabled TX enabled");
        }
        finally
        {
            GC.KeepAlive(callbacks);
            VirtualFree(window, UIntPtr.Zero, 0x8000);
            VirtualFree(image, UIntPtr.Zero, 0x8000);
        }
    }

    static void TestSequencing(byte[] code)
    {
        const int codeRva = 0x2d17000;
        const int stateRva = codeRva + StubCodeBytes;
        const int guiFixture = 0x1000;
        const int counterFixture = 0x1100;
        const int fixtureSize = stateRva + 0x1000;
        IntPtr image = VirtualAlloc(IntPtr.Zero, (UIntPtr)fixtureSize, 0x3000, 0x04);
        if (image == IntPtr.Zero) throw new Exception("Cannot allocate the sequencing fixture");
        try
        {
            WriteCode(image, codeRva, code);
            byte[] frame = { 0x53, 0x48, 0x83, 0xec, 0x20, 0x48, 0x89, 0xcb }; // save rbx, home space, use fixture as this
            WriteCode(image, guiFixture, frame);
            WriteJump(image, guiFixture + frame.Length, codeRva + BitConverter.ToInt32(code, 24 * 4));
            WriteCode(image, 0x10ee50, Decision(0)); // deferred
            WriteCode(image, 0x10eef7, Decision(1)); // start
            const int choiceFixture = guiFixture + 0x40;
            WriteCode(image, choiceFixture, frame);
            WriteJump(image, choiceFixture + frame.Length, codeRva + BitConverter.ToInt32(code, 47 * 4));
            WriteCode(image, 0x10e9f2, Decision(1)); // standard Tx1–Tx6
            WriteCode(image, 0x10d930, Decision(2)); // generated or free-form text
            const int textFixture = choiceFixture + 0x40;
            WriteCode(image, textFixture, frame);
            WriteJump(image, textFixture + frame.Length, codeRva + BitConverter.ToInt32(code, 48 * 4));
            WriteCode(image, 0x10eb38, Decision(1)); // Tx1–Tx8 text table
            WriteCode(image, 0x112c90, Decision(2)); // original default case

            for (int n = 0; n < 7; n++)
            {
                int entry = counterFixture + n * 0x20;
                WriteCode(image, entry, frame);
                WriteJump(image, entry + frame.Length, codeRva + BitConverter.ToInt32(code, (25 + n) * 4));
            }
            WriteCode(image, 0x0ab027, Decision(1)); // original CQ clear
            WriteCode(image, 0x0ab271, Decision(2)); // original call clear
            WriteCode(image, 0x0ab4e7, Decision(3)); // original report clear
            WriteCode(image, codeRva + BitConverter.ToInt32(code, 20 * 4), Decision(4)); // native halt through AutoCQ
            WriteCode(image, 0x0a8f08, Decision(5)); // normal cleanup without clearing the DX
            WriteCode(image, 0x0aa640, Decision(6)); // original SRR73 stop
            WriteCode(image, 0x0a9d00, Decision(7)); // continue with the current DX
            WriteCode(image, 0x0a8de3, Decision(8)); // original terminal counter clear
            WriteCode(image, 0x0aa753, Decision(9)); // original FIN stop
            WriteCode(image, 0x0a9f50, Decision(10)); // original S73 stop

            const int data = 0x4000;
            IntPtr mainWindow = IntPtr.Add(image, data);
            IntPtr mode = IntPtr.Add(image, data + 0x3100);
            IntPtr dx = IntPtr.Add(image, data + 0x3140);
            Marshal.WriteIntPtr(mainWindow, 0x2ae8, mode);
            Marshal.WriteIntPtr(mainWindow, 0x2a60, dx);
            Marshal.WriteInt32(mode, 4, 3);
            Marshal.WriteInt64(mode, 16, 24);
            Marshal.WriteInt32(mode, 24, 0x00540046);
            Marshal.WriteInt16(mode, 28, '8');
            Marshal.WriteInt32(dx, 4, 6);
            Marshal.WriteByte(mainWindow, 0x653, 1);
            Marshal.WriteByte(mainWindow, 0x5f3, 1);
            Marshal.WriteInt32(mainWindow, 0x4f4, 2);

            IntPtr kernel = LoadLibraryEx(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), IntPtr.Zero, 0);
            Marshal.WriteIntPtr(image, stateRva + 8, Find(kernel, "GetTickCount64"));
            uint prior;
            foreach (int page in new[] { codeRva, codeRva + 0x1000, guiFixture, 0x10d000, 0x10e000, 0x0ab000, 0x0a8000, 0x0a9000, 0x0aa000 })
                if (!VirtualProtect(IntPtr.Add(image, page), (UIntPtr)4096, 0x20, out prior))
                    throw new Exception("Cannot protect the sequencing fixture");
            NativeDecision ptt = Function<NativeDecision>(IntPtr.Add(image, guiFixture));
            Control control = Function<Control>(IntPtr.Add(image, codeRva + BitConverter.ToInt32(code, 12 * 4)));
            Check(ptt(mainWindow) == 1, "A manual initial FT8 call keeps its normal timing");
            byte[] managed = JtdxAutoCq.QtPacket.HoldNativeSelection("JTDX", 2, false, true);
            Send(control, managed);
            Check(ptt(mainWindow) == 0, "An addressed automatic FT8 reply waits for decoding before keying TX");
            Marshal.WriteByte(mainWindow, 0x5f3, 0);
            Check(ptt(mainWindow) == 1, "An addressed automatic reply can key TX as soon as decoding completes");
            Marshal.WriteByte(mainWindow, 0x5f3, 1);
            Marshal.WriteByte(mainWindow, 0x2e45, 1);
            Check(ptt(mainWindow) == 1, "The FT8 TX gate preserves Tune");
            Marshal.WriteByte(mainWindow, 0x2e45, 0);
            Send(control, JtdxAutoCq.QtPacket.HoldNativeSelection("JTDX", 2, false));
            Marshal.WriteInt32(mainWindow, 0x4f4, 4);
            Check(ptt(mainWindow) == 0, "Native terminal FT8 repeats also wait for the preceding decode");
            Marshal.WriteInt16(mode, 28, '4');
            Check(ptt(mainWindow) == 0, "The same terminal gate applies to FT4");
            Marshal.WriteInt16(mode, 28, '8');
            Marshal.WriteInt32(mainWindow, 0x4f4, 6);
            Marshal.WriteInt32(dx, 4, 0);
            Send(control, managed);
            Check(ptt(mainWindow) == 1, "The FT8 TX gate permits an ordinary CQ without DX selected");
            for (int n = 0; n < 3; n++)
            {
                NativeDecision count = Function<NativeDecision>(IntPtr.Add(image, counterFixture + n * 0x20));
                Check(count(mainWindow) == 4, "AutoCQ handles native retry limit " + (n + 1));
                Marshal.WriteInt64(image, stateRva, 0);
                Check(count(mainWindow) == n + 1, "Native retry behavior returns after lease expiry " + (n + 1));
                Send(control, managed);
            }
            var keepRr73 = Function<NativeDecision>(IntPtr.Add(image, counterFixture + 3 * 0x20));
            var keepCounter = Function<NativeDecision>(IntPtr.Add(image, counterFixture + 4 * 0x20));
            var keepFin = Function<NativeDecision>(IntPtr.Add(image, counterFixture + 5 * 0x20));
            var keepS73 = Function<NativeDecision>(IntPtr.Add(image, counterFixture + 6 * 0x20));
            Check(keepRr73(mainWindow) == 5 && keepCounter(mainWindow) == 7,
                "An armed AutoCQ session preserves RR73 through the native early stop and counter clear");
            Check(keepFin(mainWindow) == 9 && keepS73(mainWindow) == 10,
                "Ordinary incoming final 73 still reaches native FIN");
            byte[] listening = JtdxAutoCq.QtPacket.HoldNativeSelection("JTDX", 2, true, true, true);
            Send(control, listening);
            Marshal.WriteInt32(dx, 4, 6);
            Marshal.WriteInt32(mainWindow, 0x4f4, 5);
            Marshal.WriteByte(mainWindow, 0x5f3, 0);
            Check(keepFin(mainWindow) == 5 && keepS73(mainWindow) == 5 && ptt(mainWindow) == 0,
                "After our 73 the transmitter stays off while the reply window is evaluated");
            byte[] repeating = JtdxAutoCq.QtPacket.HoldNativeSelection("JTDX", 2, true, true, true, true);
            Send(control, repeating);
            Check(keepFin(mainWindow) == 5 && ptt(mainWindow) == 1,
                "Another addressed RR73 permits the existing 73 message to key TX");
            Marshal.WriteByte(mainWindow, 0x5f3, 1);
            Check(ptt(mainWindow) == 0, "A requested 73 repeat waits for the decoder");
            Marshal.WriteByte(mainWindow, 0x68b, 1);
            Check(keepRr73(mainWindow) == 6 && keepCounter(mainWindow) == 8 && keepFin(mainWindow) == 9,
                "DXpedition sequencing keeps native completion");
            Marshal.WriteByte(mainWindow, 0x68b, 0);
            Marshal.WriteInt64(image, stateRva, 0);
            Marshal.WriteByte(mainWindow, 0x5f3, 0);
            Check(keepRr73(mainWindow) == 6 && keepCounter(mainWindow) == 8 && keepFin(mainWindow) == 9
                && keepS73(mainWindow) == 10 && ptt(mainWindow) == 1,
                "Terminal hold and PTT inhibition expire with the AutoCQ lease");

            NativeDecision chooseStage = Function<NativeDecision>(IntPtr.Add(image, choiceFixture));
            NativeDecision chooseText = Function<NativeDecision>(IntPtr.Add(image, textFixture));
            var radio = new JtdxAutoCq.RadioStatus { DxCall = "DL1AAA", Mode = "FT8", Frequency = 3573000 };
            var nativeCall = radio.DxCall.ToCharArray();
            Marshal.WriteInt64(dx, 16, 24);
            Marshal.Copy(nativeCall, 0, IntPtr.Add(dx, 24), nativeCall.Length);
            Marshal.WriteInt32(dx, 4, nativeCall.Length);
            Marshal.WriteInt64(mainWindow, 0x340, (long)radio.Frequency);
            Func<int, byte[]> request = stage => JtdxAutoCq.QtPacket.HoldNativeSelection(
                "JTDX", 2, true, true, true, true, stage, radio);
            Func<NativeDecision, int, int> select = (dispatch, stage) => {
                Marshal.WriteInt32(mainWindow, 0x4f4, stage);
                dispatch(mainWindow);
                return Marshal.ReadInt32(mainWindow, 0x4f4);
            };
            Func<int, int, bool> bothSelect = (stage, expected) =>
                select(chooseStage, stage) == expected && select(chooseText, stage) == expected;

            Send(control, request(4));
            Check(ptt(mainWindow) == 1 && bothSelect(2, 4) && bothSelect(3, 4),
                "Either native path restores Tx4 if AutoSeq selects a report before an authorized RR73 repeat");
            Check(bothSelect(4, 4) && bothSelect(5, 5) && bothSelect(6, 6) && bothSelect(8, 8),
                "The sign-off correction preserves a selected terminal frame or CQ");
            Marshal.WriteInt16(dx, 24, 'F');
            Check(bothSelect(2, 2), "A late authorization cannot change another station's message");
            Marshal.WriteInt16(dx, 24, 'D');
            Marshal.WriteInt64(mainWindow, 0x340, (long)radio.Frequency + 1);
            Check(bothSelect(2, 2), "A retune invalidates the authorized retry");
            Marshal.WriteInt64(mainWindow, 0x340, (long)radio.Frequency);
            Marshal.WriteInt16(mode, 28, '4');
            Check(bothSelect(2, 2), "The FT8 retry cannot affect FT4");
            radio.Mode = "FT4";
            Send(control, request(5));
            Check(bothSelect(7, 5), "A generated FT4 report reverts to the authorized Tx5");
            Marshal.WriteByte(mainWindow, 0x2e45, 1);
            Check(bothSelect(7, 7), "Tune is untouched by the sign-off correction");
            Marshal.WriteByte(mainWindow, 0x2e45, 0);
            Marshal.WriteByte(mainWindow, 0x68b, 1);
            Check(bothSelect(7, 7), "DXpedition sequencing does not use the sign-off correction");
            Marshal.WriteByte(mainWindow, 0x68b, 0);
            Send(control, listening);
            Check(bothSelect(7, 7) && ptt(mainWindow) == 0, "The receive window clears the prior repeat request");
            Send(control, request(5));
            Marshal.WriteInt64(image, stateRva, 0);
            Check(bothSelect(7, 7), "A stale sign-off request cannot select a TX message");
        }
        finally { VirtualFree(image, UIntPtr.Zero, 0x8000); }
    }

    static byte[] Decision(byte value)
    { return new byte[] { 0xb8, value, 0, 0, 0, 0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3 }; }

    static int Main(string[] args)
    {
        byte[] code = File.ReadAllBytes(args[0]);
        IntPtr memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)(StubCodeBytes + 4096), 0x3000, 0x04);
        if (memory == IntPtr.Zero) throw new Exception("Cannot allocate harness memory");
        try
        {
            Marshal.Copy(code, 0, memory, code.Length);
            uint prior;
            if (!VirtualProtect(memory, (UIntPtr)StubCodeBytes, 0x20, out prior)) throw new Exception("Cannot protect harness code");
            Func<int, IntPtr> at = n => IntPtr.Add(memory, BitConverter.ToInt32(code, n * 4));
            Sender sender = Function<Sender>(at(7));
            Line line = Function<Line>(at(8));
            Line call = Function<Line>(at(33));
            Lease lease = Function<Lease>(at(11));
            Control control = Function<Control>(at(12));
            IntPtr kernel = LoadLibraryEx(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), IntPtr.Zero, 0);
            Marshal.WriteIntPtr(memory, StubCodeBytes + 8, Find(kernel, "GetTickCount64"));

            Check(sender(2247, 2247, 0x7f000001, 1) == 1
                && sender(2247, 2237, 0x7f000001, 1) == 0
                && sender(2237, 2237, 0x7f000001, 1) == 0
                && sender(2247, 2247, 0xc0a80001, 1) == 0
                && sender(2247, 2247, 0x7f000001, 0) == 0,
                "Queue commands require the loopback AutoCQ port");
            Check(VerifyLine(line, "120015 -18  0.2 1004 ~ K1ABC SV9AUP KM25") == 1
                && VerifyLine(line, "120015 -18  0.2 1004 : K1ABC SV9AUP KM25") == 1
                && VerifyLine(line, "120015 -18  0.2 1004 ~ K1ABC PA40XYZAB") == 1
                && VerifyLine(line, "120015 -18  0.2 1004 : <K1ABC> PA40XYZAB") == 1
                && VerifyLine(line, "120015 -18  0.2 1004 ~ K1ABC SV9AUP\nKM25") == 0
                && VerifyLine(line, "120015 -18  0.2 1004 # K1ABC SV9AUP KM25") == 0,
                "Saved-line path accepts FT8 and FT4 with or without a payload and rejects line injection and other modes");
            Check(VerifyLine(call, "9J2ZZ") == 1 && VerifyLine(call, "W3ABC/P") == 1
                && VerifyLine(call, "W3ABC\nCQ") == 0 && VerifyLine(call, "/W3ABC") == 0
                && VerifyLine(call, "W3ABC/") == 0 && VerifyLine(call, "W3/ABC/P") == 0
                && VerifyLine(call, "WABC") == 0 && VerifyLine(call, "123") == 0,
                "Direct-call validation accepts real callsigns and rejects malformed payloads");
            byte[] renew = JtdxAutoCq.QtPacket.HoldNativeSelection("JTDX", 2, true);
            Send(control, renew);
            ulong now = GetTickCount64();
            Check(lease(now) == 1 && lease(now + 2600) == 0, "Native selection pause expires automatically");
            byte[] wrongId = JtdxAutoCq.QtPacket.HoldNativeSelection("FAKE", 2, false);
            Send(control, wrongId);
            Check(lease(now) == 1, "Selection release validates the JTDX packet ID");
            Send(control, JtdxAutoCq.QtPacket.HoldNativeSelection("JTDX", 3, false));
            Check(lease(now) == 0, "Stopping the pause restores native selection immediately");
            TestQt(args[1]);
            TestPeMapping(args[2], args[1]);
            TestReplyGate(code);
            TestDirectCall(code);
            TestSequencing(code);
            return 0;
        }
        finally { VirtualFree(memory, UIntPtr.Zero, 0x8000); }
    }
}
