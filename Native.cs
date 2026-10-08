using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeHistory;

enum Shell { PowerShell, Cmd, Posix }

abstract record InputEvent;
sealed record KeyInput(ConsoleKeyInfo Key) : InputEvent;
sealed record MouseInput(int X, int Y, MouseAction Action, int Wheel) : InputEvent;
enum MouseAction { Click, DoubleClick, Wheel }

static partial class Native
{
    // ---- console ----

    const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11;

    [LibraryImport("kernel32.dll")] private static partial IntPtr GetStdHandle(int nStdHandle);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetConsoleMode(IntPtr h, out uint mode);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleMode(IntPtr h, uint mode);

    public static void EnableVirtualTerminal()
    {
        var h = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(h, out var mode)) SetConsoleMode(h, mode | 0x0004 /* ENABLE_VIRTUAL_TERMINAL_PROCESSING */);
    }

    /// <summary>INPUT_RECORD: the key and mouse variants of the union share offsets.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        // KEY_EVENT_RECORD
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort RepeatCount;
        [FieldOffset(10)] public ushort VirtualKeyCode;
        [FieldOffset(12)] public ushort VirtualScanCode;
        [FieldOffset(14)] public ushort UnicodeChar;
        [FieldOffset(16)] public uint ControlKeyState;
        // MOUSE_EVENT_RECORD
        [FieldOffset(4)] public short MouseX;
        [FieldOffset(6)] public short MouseY;
        [FieldOffset(8)] public uint ButtonState;
        [FieldOffset(12)] public uint MouseControlKeyState;
        [FieldOffset(16)] public uint EventFlags;
    }

    // ---- raw input: keys and mouse straight from the console input buffer ----

    [LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleInputW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadConsoleInput(IntPtr h, [Out] InputRecord[] buffer, uint length, out uint read);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNumberOfConsoleInputEvents(IntPtr h, out uint count);

    static uint savedInputMode;

    /// <summary>Mouse on; quick-edit, line buffering, echo and Ctrl+C processing off (Ctrl+C arrives as a key).</summary>
    public static void EnterRawInput()
    {
        var h = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(h, out savedInputMode);
        const uint processed = 0x1, line = 0x2, echo = 0x4, window = 0x8, mouse = 0x10, quickEdit = 0x40, extended = 0x80, vtInput = 0x200;
        SetConsoleMode(h, (savedInputMode & ~(processed | line | echo | quickEdit | vtInput)) | window | mouse | extended);
    }

    public static void RestoreInput() => SetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), savedInputMode);

    /// <summary>Everything waiting in the input buffer, without blocking.</summary>
    public static List<InputEvent> ReadInput()
    {
        var events = new List<InputEvent>();
        var h = GetStdHandle(STD_INPUT_HANDLE);
        if (!GetNumberOfConsoleInputEvents(h, out var count) || count == 0) return events;

        var buffer = new InputRecord[Math.Min(count, 512u)];
        if (!ReadConsoleInput(h, buffer, (uint)buffer.Length, out var read)) return events;

        for (int i = 0; i < read; i++)
        {
            var r = buffer[i];
            if (r.EventType == 1 && r.KeyDown != 0)
            {
                if (r.VirtualKeyCode is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C) continue; // bare modifier presses
                uint s = r.ControlKeyState;
                var key = new ConsoleKeyInfo((char)r.UnicodeChar, (ConsoleKey)r.VirtualKeyCode,
                    shift: (s & 0x10) != 0, alt: (s & 0x3) != 0, control: (s & 0xC) != 0);
                for (int n = 0; n < Math.Max((int)r.RepeatCount, 1); n++) events.Add(new KeyInput(key));
            }
            else if (r.EventType == 2)
            {
                const uint doubleClick = 0x2, wheeled = 0x4;
                if ((r.EventFlags & wheeled) != 0)
                    events.Add(new MouseInput(r.MouseX, r.MouseY, MouseAction.Wheel, (short)(r.ButtonState >> 16) > 0 ? 1 : -1));
                else if ((r.ButtonState & 1) != 0 && r.EventFlags is 0 or doubleClick)
                    events.Add(new MouseInput(r.MouseX, r.MouseY, r.EventFlags == doubleClick ? MouseAction.DoubleClick : MouseAction.Click, 0));
            }
        }
        return events;
    }

    // ---- keystroke injection: queue a command line into the console input so the parent shell runs it after we exit ----

    [LibraryImport("kernel32.dll", EntryPoint = "WriteConsoleInputW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteConsoleInput(IntPtr h, InputRecord[] records, uint length, out uint written);

    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanW")] private static partial short VkKeyScan(ushort ch);

    public static bool InjectInput(string text)
    {
        var records = new List<InputRecord>();
        foreach (var ch in text + "\r")
        {
            ushort vk = 0;
            uint state = 0;
            if (ch == '\r') vk = 0x0D;
            else
            {
                short scan = VkKeyScan((ushort)ch);
                if (scan != -1)
                {
                    vk = (ushort)(scan & 0xFF);
                    if ((scan & 0x100) != 0) state |= 0x0010; // SHIFT_PRESSED
                }
            }
            foreach (var down in new[] { 1, 0 })
                records.Add(new InputRecord { EventType = 1, KeyDown = down, RepeatCount = 1, VirtualKeyCode = vk, UnicodeChar = (ushort)ch, ControlKeyState = state });
        }
        var arr = records.ToArray();
        return WriteConsoleInput(GetStdHandle(STD_INPUT_HANDLE), arr, (uint)arr.Length, out var written) && written == arr.Length;
    }

    // ---- clipboard ----

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(IntPtr owner);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] private static partial IntPtr SetClipboardData(uint format, IntPtr mem);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GlobalLock(IntPtr mem);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(IntPtr mem);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GlobalFree(IntPtr mem);

    public static bool CopyToClipboard(string text)
    {
        for (int i = 0; i < 10 && !OpenClipboard(IntPtr.Zero); i++) Thread.Sleep(20);
        try
        {
            if (!EmptyClipboard()) return false;
            var mem = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (nuint)((text.Length + 1) * 2));
            if (mem == IntPtr.Zero) return false;
            var ptr = GlobalLock(mem);
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0);
            GlobalUnlock(mem);
            if (SetClipboardData(13 /* CF_UNICODETEXT */, mem) == IntPtr.Zero) { GlobalFree(mem); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }

    // ---- parent shell detection ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static Shell DetectShell()
    {
        try
        {
            var name = ParentProcessName(Environment.ProcessId)?.ToLowerInvariant() ?? "";
            if (name.StartsWith("cmd")) return Shell.Cmd;
            if (name.StartsWith("bash") || name.StartsWith("sh.") || name.StartsWith("zsh") || name.StartsWith("fish")) return Shell.Posix;
        }
        catch { }
        return Shell.PowerShell;
    }

    static string? ParentProcessName(int pid)
    {
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return null;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
                if (entry.th32ProcessID == pid)
                    return Process.GetProcessById((int)entry.th32ParentProcessID).ProcessName;
        }
        finally { CloseHandle(snap); }
        return null;
    }

    static string Claude(Session s, bool fork) => $"claude --resume {s.Id}" + (fork ? " --fork-session" : "");

    public static string ResumeCommand(Session s, Shell shell, bool fork)
    {
        var dir = s.Folder;
        return shell switch
        {
            Shell.Cmd => $"cd /d \"{dir}\" && {Claude(s, fork)}",
            Shell.Posix => $"cd \"{PosixPath(dir)}\" && {Claude(s, fork)}",
            _ => $"cd '{dir.Replace("'", "''")}'; {Claude(s, fork)}",
        };
    }

    /// <summary>Opens a new Windows Terminal tab in the session's folder running the resume; the tab stays open as a shell after Claude exits.</summary>
    public static bool OpenInNewTab(Session s, Shell shell, bool fork)
    {
        try
        {
            var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            foreach (var a in new[] { "-w", "0", "new-tab", "-d", s.Folder }) psi.ArgumentList.Add(a);
            var shellArgs = shell == Shell.Cmd
                ? new[] { "cmd.exe", "/k", Claude(s, fork) }
                : ["powershell.exe", "-NoLogo", "-NoExit", "-Command", Claude(s, fork)];
            foreach (var a in shellArgs) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch { return false; }
    }

    static string PosixPath(string p) =>
        p.Length >= 2 && p[1] == ':' ? "/" + char.ToLowerInvariant(p[0]) + p[2..].Replace('\\', '/') : p.Replace('\\', '/');
}
