using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Execute the assembled validation routines in a disposable process. Load
// only the Qt DLL used for QString, with no JTDX process or radio connection.
static class TestNativeQueue
{
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
        }
        finally { FreeLibrary(image); }
    }

    static void TestReplyGate(byte[] code)
    {
        const int codeRva = 0x2d17000;
        const int entry = 0x1000;
        const int fixtureSize = codeRva + 0x1000;
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

    static void TestSequencing(byte[] code)
    {
        const int codeRva = 0x2d17000;
        const int stateRva = 0x2d18000;
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
            foreach (int page in new[] { codeRva, guiFixture, 0x10e000, 0x0ab000, 0x0a8000, 0x0a9000, 0x0aa000 })
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
        }
        finally { VirtualFree(image, UIntPtr.Zero, 0x8000); }
    }

    static byte[] Decision(byte value)
    { return new byte[] { 0xb8, value, 0, 0, 0, 0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3 }; }

    static int Main(string[] args)
    {
        byte[] code = File.ReadAllBytes(args[0]);
        IntPtr memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)8192, 0x3000, 0x04);
        if (memory == IntPtr.Zero) throw new Exception("Cannot allocate harness memory");
        try
        {
            Marshal.Copy(code, 0, memory, code.Length);
            uint prior;
            if (!VirtualProtect(memory, (UIntPtr)4096, 0x20, out prior)) throw new Exception("Cannot protect harness code");
            Func<int, IntPtr> at = n => IntPtr.Add(memory, BitConverter.ToInt32(code, n * 4));
            Sender sender = Function<Sender>(at(7));
            Line line = Function<Line>(at(8));
            Lease lease = Function<Lease>(at(11));
            Control control = Function<Control>(at(12));
            IntPtr kernel = LoadLibraryEx(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), IntPtr.Zero, 0);
            Marshal.WriteIntPtr(memory, 4096 + 8, Find(kernel, "GetTickCount64"));

            Check(sender(2247, 2247, 0x7f000001, 1) == 1
                && sender(2247, 2237, 0x7f000001, 1) == 0
                && sender(2237, 2237, 0x7f000001, 1) == 0
                && sender(2247, 2247, 0xc0a80001, 1) == 0
                && sender(2247, 2247, 0x7f000001, 0) == 0,
                "Queue commands require the loopback AutoCQ port");
            Check(VerifyLine(line, "120015 -18  0.2 1004 ~ K1ABC SV9AUP KM25") == 1
                && VerifyLine(line, "120015 -18  0.2 1004 : K1ABC SV9AUP KM25") == 1
                && VerifyLine(line, "120015 -18  0.2 1004 ~ K1ABC SV9AUP\nKM25") == 0
                && VerifyLine(line, "120015 -18  0.2 1004 # K1ABC SV9AUP KM25") == 0,
                "Saved-line path accepts FT8 and FT4 and rejects line injection and other modes");
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
            TestSequencing(code);
            return 0;
        }
        finally { VirtualFree(memory, UIntPtr.Zero, 0x8000); }
    }
}
