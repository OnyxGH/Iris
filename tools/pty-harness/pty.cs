// ConPTY harness: pty.cs <outFile> <cols> <rows> <scriptFile> -- <exe> [args...]
// Script lines: "wait <ms>", "send <text with \e \r \n \t \x03 escapes>", "until <substring> <timeoutMs>"
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

var outFile = args[0];
short cols = short.Parse(args[1]);
short rows = short.Parse(args[2]);
var script = File.ReadAllLines(args[3]);
var sep = Array.IndexOf(args, "--");
var cmdLine = string.Join(" ", args.Skip(sep + 1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0);
Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0);
var hr = Native.CreatePseudoConsole(new Native.COORD { X = cols, Y = rows }, inRead, outWrite, 0, out var hpc);
if (hr != 0) throw new Win32Exception(hr);

var si = new Native.STARTUPINFOEX();
si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
si.StartupInfo.dwFlags = 0x100;
IntPtr size = IntPtr.Zero;
Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
si.lpAttributeList = Marshal.AllocHGlobal(size);
Native.InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size);
Native.UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)0x00020016, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);
if (!Native.CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, null, ref si, out var pi))
    throw new Win32Exception(Marshal.GetLastWin32Error());

var output = new StringBuilder();
var outStream = new FileStream(outRead, FileAccess.Read);
var reader = new Thread(() =>
{
    var buf = new byte[8192];
    var decoder = Encoding.UTF8.GetDecoder();
    var chars = new char[16384];
    while (true)
    {
        int n;
        try { n = outStream.Read(buf, 0, buf.Length); } catch { break; }
        if (n <= 0) break;
        var c = decoder.GetChars(buf, 0, n, chars, 0);
        lock (output) output.Append(chars, 0, c);
    }
}) { IsBackground = true };
reader.Start();
var inStream = new FileStream(inWrite, FileAccess.Write);

string Unescape(string s) => Regex.Replace(s, @"\\(e|r|n|t|x[0-9a-fA-F]{2}|\\)", m => m.Groups[1].Value switch
{
    "e" => "\x1b", "r" => "\r", "n" => "\n", "t" => "\t", "\\" => "\\",
    var x => ((char)Convert.ToInt32(x[1..], 16)).ToString(),
});

string Plain() { lock (output) return Regex.Replace(output.ToString(), @"\x1b(\[[0-9;?<>=]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)|_[^\x07\x1b]*(\x07|\x1b\\)|P[^\x1b]*\x1b\\|[()][A-Z0-9]|[=>78DEHMc])", ""); }

foreach (var raw in script)
{
    var line = raw.TrimEnd();
    if (line.Length == 0 || line.StartsWith('#')) continue;
    var sp = line.IndexOf(' ');
    var cmd = sp < 0 ? line : line[..sp];
    var rest = sp < 0 ? "" : line[(sp + 1)..];
    switch (cmd)
    {
        case "screen":
            lock (output) { var scrLines = Vt.Render(output.ToString(), cols, rows); Console.WriteLine("=== screen " + rest + " ==="); for (var k = 0; k < scrLines.Count; k++) Console.WriteLine(k.ToString().PadLeft(2) + "|" + scrLines[k]); }
            break;
        case "waitgone":
        {
            // Wait until the emulated screen no longer contains the text (e.g. a "Working" status).
            var goneSp = rest.LastIndexOf(' ');
            var goneText = rest[..goneSp]; var goneTimeout = int.Parse(rest[(goneSp + 1)..]);
            bool OnScreen() { lock (output) return Vt.Render(output.ToString(), cols, rows).Any(l => l.Contains(goneText)); }
            var goneSw = System.Diagnostics.Stopwatch.StartNew();
            while (OnScreen() && goneSw.ElapsedMilliseconds < goneTimeout) Thread.Sleep(250);
            Console.WriteLine(OnScreen() ? $"TIMEOUT waiting for removal of: {goneText}" : $"gone: {goneText} ({goneSw.ElapsedMilliseconds}ms)");
            break;
        }
        case "wait": Thread.Sleep(int.Parse(rest)); break;
        case "send":
            var bytes = Encoding.UTF8.GetBytes(Unescape(rest));
            inStream.Write(bytes); inStream.Flush();
            break;
        case "until":
            var lastSp = rest.LastIndexOf(' ');
            var needle = rest[..lastSp]; var timeout = int.Parse(rest[(lastSp + 1)..]);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Plain().Contains(needle) && sw.ElapsedMilliseconds < timeout) Thread.Sleep(50);
            Console.WriteLine(Plain().Contains(needle) ? $"found: {needle} ({sw.ElapsedMilliseconds}ms)" : $"TIMEOUT waiting for: {needle}");
            break;
    }
}

var exited = Native.WaitForSingleObject(pi.hProcess, 5000) == 0;
Console.WriteLine(exited ? $"process exited, code {(Native.GetExitCodeProcess(pi.hProcess, out var code) ? code : 0)}" : "process still running; killing");
if (!exited) Native.TerminateProcess(pi.hProcess, 1);
Native.ClosePseudoConsole(hpc);
Thread.Sleep(300);
lock (output)
{
    File.WriteAllText(outFile, output.ToString());
    File.WriteAllText(outFile + ".txt", Plain());
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X; public short Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CreatePipe(out SafeFileHandle r, out SafeFileHandle w, IntPtr sa, int size);
    [DllImport("kernel32.dll")] public static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] public static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool CreateProcess(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
}
static class Vt
{
    public static List<string> Render(string data, int cols, int rows)
    {
        var scr = new List<char[]>();
        for (var i = 0; i < rows; i++) scr.Add(Enumerable.Repeat(' ', cols).ToArray());
        int x = 0, y = 0, top = 0, bottom = rows - 1;
        void ScrollUp() { scr.RemoveAt(top); scr.Insert(bottom, Enumerable.Repeat(' ', cols).ToArray()); }
        var i2 = 0;
        while (i2 < data.Length)
        {
            var ch = data[i2];
            if (ch == '\x1b' && i2 + 1 < data.Length)
            {
                var n = data[i2 + 1];
                if (n == '[')
                {
                    var j = i2 + 2; while (j < data.Length && (data[j] < '@' || data[j] > '~')) j++;
                    if (j >= data.Length) break;
                    var p = data[(i2 + 2)..j]; var f = data[j]; i2 = j + 1;
                    var priv = p.StartsWith('?'); if (priv) p = p[1..];
                    var ps = p.Split(';').Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
                    int P(int k, int d) => k < ps.Length && ps[k] > 0 ? ps[k] : d;
                    if (priv) continue;
                    switch (f)
                    {
                        case 'A': y = Math.Max(0, y - P(0, 1)); break;
                        case 'B': y = Math.Min(rows - 1, y + P(0, 1)); break;
                        case 'C': x = Math.Min(cols - 1, x + P(0, 1)); break;
                        case 'D': x = Math.Max(0, x - P(0, 1)); break;
                        case 'G': x = Math.Min(cols - 1, P(0, 1) - 1); break;
                        case 'H': case 'f': y = Math.Min(rows - 1, P(0, 1) - 1); x = Math.Min(cols - 1, P(1, 1) - 1); break;
                        case 'd': y = Math.Min(rows - 1, P(0, 1) - 1); break;
                        case 'J':
                            var mJ = ps.Length > 0 ? ps[0] : 0;
                            if (mJ == 2 || mJ == 3) foreach (var r in scr) Array.Fill(r, ' ');
                            else if (mJ == 0) { Array.Fill(scr[y], ' ', x, cols - x); for (var r = y + 1; r < rows; r++) Array.Fill(scr[r], ' '); }
                            break;
                        case 'K':
                            var mK = ps.Length > 0 ? ps[0] : 0;
                            if (mK == 0) Array.Fill(scr[y], ' ', x, cols - x); else if (mK == 1) Array.Fill(scr[y], ' ', 0, x + 1); else Array.Fill(scr[y], ' ');
                            break;
                        case 'X': Array.Fill(scr[y], ' ', x, Math.Min(P(0, 1), cols - x)); break;
                        case 'r': top = P(0, 1) - 1; bottom = P(1, rows) - 1; break;
                        case 'S': for (var k = 0; k < P(0, 1); k++) ScrollUp(); break;
                        case 'L': for (var k = 0; k < P(0, 1); k++) { scr.RemoveAt(bottom); scr.Insert(y, Enumerable.Repeat(' ', cols).ToArray()); } break;
                        case 'M': for (var k = 0; k < P(0, 1); k++) { scr.RemoveAt(y); scr.Insert(bottom, Enumerable.Repeat(' ', cols).ToArray()); } break;
                        case 'P': { var c = P(0, 1); var row = scr[y]; Array.Copy(row, Math.Min(cols, x + c), row, x, Math.Max(0, cols - x - c)); Array.Fill(row, ' ', Math.Max(x, cols - c), Math.Min(c, cols - x)); } break;
                        case '@': { var c = P(0, 1); var row = scr[y]; Array.Copy(row, x, row, Math.Min(cols, x + c), Math.Max(0, cols - x - c)); Array.Fill(row, ' ', x, Math.Min(c, cols - x)); } break;
                    }
                    continue;
                }
                if (n == ']' || n == '_' || n == 'P')
                {
                    var j = i2 + 2; while (j < data.Length && data[j] != '\x07' && !(data[j] == '\x1b' && j + 1 < data.Length && data[j + 1] == (char)92)) j++;
                    i2 = j < data.Length && data[j] == '\x07' ? j + 1 : j + 2; continue;
                }
                if (n == 'M') { if (y == top) { scr.RemoveAt(bottom); scr.Insert(top, Enumerable.Repeat(' ', cols).ToArray()); } else y = Math.Max(0, y - 1); }
                i2 += (n == '(' || n == ')') ? 3 : 2; continue;
            }
            i2++;
            if (ch == '\r') { x = 0; continue; }
            if (ch == '\n') { if (y == bottom) ScrollUp(); else y = Math.Min(rows - 1, y + 1); continue; }
            if (ch == '\b') { x = Math.Max(0, x - 1); continue; }
            if (ch < ' ') continue;
            if (x >= cols) { x = 0; if (y == bottom) ScrollUp(); else y++; }
            scr[y][x] = ch; x++;
        }
        return scr.Select(r => new string(r).TrimEnd()).ToList();
    }
}
