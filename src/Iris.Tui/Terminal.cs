using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Iris.Tui;

/// <summary>Minimal terminal interface for the TUI.</summary>
public interface ITerminal
{
    /// <summary>Start with input and resize handlers. Handlers are invoked on the UI dispatcher.</summary>
    void Start(Action<string> onInput, Action onResize);

    void Stop();

    /// <summary>Drain stdin before exiting so late key release events do not leak to the parent shell.</summary>
    Task DrainInputAsync(int maxMs = 1000, int idleMs = 50);

    void Write(string data);

    int Columns { get; }
    int Rows { get; }
    bool KittyProtocolActive { get; }

    void MoveBy(int lines);
    void HideCursor();
    void ShowCursor();
    void ClearLine();
    void ClearFromCursor();
    void ClearScreen();
    void SetTitle(string title);
    void SetProgress(bool active);
}

public abstract record KeyboardProtocolNegotiation;

public sealed record KittyFlagsNegotiation(int Flags) : KeyboardProtocolNegotiation;

public sealed record DeviceAttributesNegotiation : KeyboardProtocolNegotiation;

/// <summary>Real terminal on the process console (Windows console VT mode or POSIX termios raw mode).</summary>
public sealed partial class ProcessTerminal : ITerminal
{
    private const string ProgressActive = "\e]9;4;3\a";
    private const string ProgressClear = "\e]9;4;0\a";
    private const string NativeShiftEnter = "\e[13;2u";
    private const int DesiredKittyFlags = 7;
    private const int NegotiationFragmentTimeoutMs = 150;
    private static readonly string KittyQuery = $"\e[>{DesiredKittyFlags}u\e[?u\e[c";

    private Action<string>? _inputHandler;
    private Action? _resizeHandler;
    private bool _kittyActive;
    private bool _modifyOtherKeysActive;
    private bool _keyboardProtocolPushed;
    private string _negotiationBuffer = "";
    private IDisposable? _negotiationTimer;
    private StdinBuffer? _stdinBuffer;
    private IDisposable? _progressInterval;
    private IDisposable? _resizePoll;
    private PosixSignalRegistration? _sigwinch;
    private Thread? _readerThread;
    private volatile bool _reading;
    private long _lastDataTicks;
    private (int Columns, int Rows) _lastSize;
    private readonly Stream _stdout;
    private readonly object _writeLock = new();
    private readonly string _writeLogPath;
    private UiDispatcher? _dispatcher;
    private IRawMode? _rawMode;

    public ProcessTerminal()
    {
        _stdout = Console.OpenStandardOutput();
        var log = Environment.GetEnvironmentVariable("IRIS_TUI_WRITE_LOG") ?? "";
        _writeLogPath = log.Length > 0 && Directory.Exists(log)
            ? Path.Combine(log, $"tui-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Environment.ProcessId}.log")
            : log;
    }

    public bool KittyProtocolActive => _kittyActive;

    public bool ModifyOtherKeysActive => _modifyOtherKeysActive;

    [GeneratedRegex("^\\e\\[\\?(\\d+)u$")]
    private static partial Regex KittyFlagsResponse();

    [GeneratedRegex("^\\e\\[\\?[\\d;]*c$")]
    private static partial Regex DeviceAttributesResponse();

    [GeneratedRegex("^\\e\\[\\?[\\d;]*$")]
    private static partial Regex NegotiationPrefix();

    public static KeyboardProtocolNegotiation? ParseKeyboardProtocolNegotiationSequence(string sequence)
    {
        var m = KittyFlagsResponse().Match(sequence);
        if (m.Success) return new KittyFlagsNegotiation(int.Parse(m.Groups[1].Value));
        return DeviceAttributesResponse().IsMatch(sequence) ? new DeviceAttributesNegotiation() : null;
    }

    private static bool IsNegotiationPrefix(string sequence) => sequence == "\e[" || NegotiationPrefix().IsMatch(sequence);

    public static int ResolveEscapeTimeoutMs()
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("IRIS_TUI_ESC_TIMEOUT"), out var configured) && configured > 0) return (int)configured;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"))) return 100;
        return 10;
    }

    private void Post(Action action)
    {
        if (_dispatcher is { } d) d.Post(action);
        else action();
    }

    public void Start(Action<string> onInput, Action onResize)
    {
        _dispatcher = UiDispatcher.Current;
        _inputHandler = onInput;
        _resizeHandler = onResize;

        _rawMode = OperatingSystem.IsWindows() ? new WindowsRawMode() : new PosixRawMode();
        _rawMode.Enable();

        Write("\e[?2004h");

        _lastSize = (Columns, Rows);
        if (OperatingSystem.IsWindows())
        {
            _resizePoll = new Timer(_ =>
            {
                var size = (Columns, Rows);
                if (size == _lastSize) return;
                _lastSize = size;
                Post(() => _resizeHandler?.Invoke());
            }, null, 100, 100);
        }
        else
        {
            _sigwinch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, ctx =>
            {
                ctx.Cancel = true;
                Post(() => _resizeHandler?.Invoke());
            });
        }

        QueryAndEnableKittyProtocol();
    }

    private void SetupStdinBuffer()
    {
        _stdinBuffer = new StdinBuffer(escapeTimeoutMs: ResolveEscapeTimeoutMs()) { Post = Post };
        _stdinBuffer.Data += sequence =>
        {
            var negotiation = ReadNegotiationSequence(sequence, out var pending);
            if (pending)
            {
                ScheduleNegotiationFlush();
                return;
            }
            if (HandleNegotiation(negotiation)) return;
            ForwardInputSequence(sequence);
        };
        _stdinBuffer.Paste += content => _inputHandler?.Invoke($"\e[200~{content}\e[201~");
    }

    private void QueryAndEnableKittyProtocol()
    {
        SetupStdinBuffer();
        StartReader();
        _keyboardProtocolPushed = true;
        ClearNegotiationBuffer();
        Write(KittyQuery);
    }

    private void StartReader()
    {
        _reading = true;
        _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "iris-stdin" };
        _readerThread.Start();
    }

    private void ReaderLoop()
    {
        if (OperatingSystem.IsWindows())
        {
            var buffer = new char[4096];
            while (_reading)
            {
                var read = WindowsRawMode.ReadConsole(buffer);
                if (read <= 0)
                {
                    if (read < 0) break;
                    continue;
                }
                Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
                var text = new string(buffer, 0, read);
                Post(() =>
                {
                    if (_reading) _stdinBuffer?.Process(text);
                });
            }
            return;
        }

        using var stdin = Console.OpenStandardInput();
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[8192];
        while (_reading)
        {
            int n;
            try
            {
                n = stdin.Read(bytes, 0, bytes.Length);
            }
            catch (IOException)
            {
                break;
            }
            if (n <= 0) break;
            Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
            string text;
            if (n == 1 && bytes[0] > 127 && decoder.GetCharCount(bytes, 0, 1, flush: false) == 0)
            {
                // High-byte meta: ESC + (byte - 128).
                text = $"\e{(char)(bytes[0] - 128)}";
                decoder.Reset();
            }
            else
            {
                var count = decoder.GetChars(bytes, 0, n, chars, 0, flush: false);
                text = new string(chars, 0, count);
            }
            Post(() =>
            {
                if (_reading) _stdinBuffer?.Process(text);
            });
        }
    }

    private bool HandleNegotiation(KeyboardProtocolNegotiation? negotiation)
    {
        if (negotiation is null) return false;
        ClearNegotiationBuffer();
        if (negotiation is KittyFlagsNegotiation kitty)
        {
            if (kitty.Flags != 0)
            {
                DisableModifyOtherKeys();
                if (!_kittyActive)
                {
                    _kittyActive = true;
                    Keys.SetKittyProtocolActive(true);
                }
            }
            else
            {
                EnableModifyOtherKeys();
            }
            return true;
        }
        if (!_kittyActive) EnableModifyOtherKeys();
        return true;
    }

    private KeyboardProtocolNegotiation? ReadNegotiationSequence(string sequence, out bool pending)
    {
        pending = false;
        if (_negotiationBuffer.Length > 0)
        {
            var buffered = _negotiationBuffer + sequence;
            if (ParseKeyboardProtocolNegotiationSequence(buffered) is { } parsed)
            {
                ClearNegotiationBuffer();
                return parsed;
            }
            if (IsNegotiationPrefix(buffered))
            {
                SetNegotiationBuffer(buffered);
                pending = true;
                return null;
            }
            FlushNegotiationBufferAsInput();
        }

        if (ParseKeyboardProtocolNegotiationSequence(sequence) is { } direct) return direct;
        if (IsNegotiationPrefix(sequence))
        {
            SetNegotiationBuffer(sequence);
            pending = true;
        }
        return null;
    }

    private void SetNegotiationBuffer(string sequence)
    {
        ClearNegotiationTimer();
        _negotiationBuffer = sequence;
    }

    private void ClearNegotiationBuffer()
    {
        ClearNegotiationTimer();
        _negotiationBuffer = "";
    }

    private void FlushNegotiationBufferAsInput()
    {
        if (_negotiationBuffer.Length == 0) return;
        var sequence = _negotiationBuffer;
        ClearNegotiationBuffer();
        ForwardInputSequence(sequence);
    }

    private void ScheduleNegotiationFlush()
    {
        if (_negotiationBuffer.Length == 0 || _negotiationTimer is not null) return;
        Action flush = () =>
        {
            _negotiationTimer = null;
            FlushNegotiationBufferAsInput();
        };
        _negotiationTimer = _dispatcher?.SetTimeout(flush, NegotiationFragmentTimeoutMs);
    }

    private void ClearNegotiationTimer()
    {
        _negotiationTimer?.Dispose();
        _negotiationTimer = null;
    }

    private void ForwardInputSequence(string sequence)
    {
        if (_inputHandler is null) return;
        var detectNativeShiftEnter = sequence == "\r" && (OperatingSystem.IsWindows() || IsAppleTerminalSession());
        var input = detectNativeShiftEnter && NativeModifiers.IsShiftPressed() ? NativeShiftEnter : sequence;
        _inputHandler(input);
    }

    public static bool IsAppleTerminalSession() =>
        OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("TERM_PROGRAM") == "Apple_Terminal";

    private void EnableModifyOtherKeys()
    {
        if (_kittyActive || _modifyOtherKeysActive) return;
        Write("\e[>4;2m");
        _modifyOtherKeysActive = true;
    }

    private void DisableModifyOtherKeys()
    {
        if (!_modifyOtherKeysActive) return;
        Write("\e[>4;0m");
        _modifyOtherKeysActive = false;
    }

    public async Task DrainInputAsync(int maxMs = 1000, int idleMs = 50)
    {
        var disableKitty = _keyboardProtocolPushed || _kittyActive;
        ClearNegotiationBuffer();
        if (disableKitty)
        {
            Write("\e[<u");
            _keyboardProtocolPushed = false;
            _kittyActive = false;
            Keys.SetKittyProtocolActive(false);
        }
        DisableModifyOtherKeys();

        var previous = _inputHandler;
        _inputHandler = null;
        Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
        var endTime = Environment.TickCount64 + maxMs;
        try
        {
            while (true)
            {
                var now = Environment.TickCount64;
                var timeLeft = endTime - now;
                if (timeLeft <= 0) break;
                if (now - Interlocked.Read(ref _lastDataTicks) >= idleMs) break;
                await Task.Delay((int)Math.Min(idleMs, timeLeft)).ConfigureAwait(true);
            }
        }
        finally
        {
            _inputHandler = previous;
        }
    }

    public void Stop()
    {
        if (ClearProgressInterval()) Write(ProgressClear);
        Write("\e[?2004l");

        var disableKitty = _keyboardProtocolPushed || _kittyActive;
        ClearNegotiationBuffer();
        if (disableKitty)
        {
            Write("\e[<u");
            _keyboardProtocolPushed = false;
            _kittyActive = false;
            Keys.SetKittyProtocolActive(false);
        }
        DisableModifyOtherKeys();

        _stdinBuffer?.Dispose();
        _stdinBuffer = null;
        _reading = false;
        if (OperatingSystem.IsWindows()) WindowsRawMode.CancelRead();
        _inputHandler = null;
        _resizeHandler = null;
        _resizePoll?.Dispose();
        _resizePoll = null;
        _sigwinch?.Dispose();
        _sigwinch = null;

        _rawMode?.Restore();
        _rawMode = null;
    }

    public void Write(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        lock (_writeLock)
        {
            _stdout.Write(bytes);
            _stdout.Flush();
        }
        if (_writeLogPath.Length > 0)
        {
            try
            {
                File.AppendAllText(_writeLogPath, data);
            }
            catch
            {
                // Ignore logging errors.
            }
        }
    }

    public int Columns
    {
        get
        {
            try
            {
                var w = Console.WindowWidth;
                if (w > 0) return w;
            }
            catch
            {
                // Not a console.
            }
            return int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var c) && c > 0 ? c : 80;
        }
    }

    public int Rows
    {
        get
        {
            try
            {
                var h = Console.WindowHeight;
                if (h > 0) return h;
            }
            catch
            {
                // Not a console.
            }
            return int.TryParse(Environment.GetEnvironmentVariable("LINES"), out var l) && l > 0 ? l : 24;
        }
    }

    public void MoveBy(int lines)
    {
        if (lines > 0) Write($"\e[{lines}B");
        else if (lines < 0) Write($"\e[{-lines}A");
    }

    public void HideCursor() => Write("\e[?25l");

    public void ShowCursor() => Write("\e[?25h");

    public void ClearLine() => Write("\e[K");

    public void ClearFromCursor() => Write("\e[J");

    public void ClearScreen() => Write("\e[2J\e[H");

    public void SetTitle(string title) => Write($"\e]0;{title}\a");

    public void SetProgress(bool active)
    {
        if (active)
        {
            Write(ProgressActive);
            _progressInterval ??= new Timer(_ => Write(ProgressActive), null, 1000, 1000);
        }
        else
        {
            ClearProgressInterval();
            Write(ProgressClear);
        }
    }

    private bool ClearProgressInterval()
    {
        if (_progressInterval is null) return false;
        _progressInterval.Dispose();
        _progressInterval = null;
        return true;
    }

    private interface IRawMode
    {
        void Enable();
        void Restore();
    }

    private sealed class WindowsRawMode : IRawMode
    {
        private const int StdInputHandle = -10;
        private const int StdOutputHandle = -11;
        private const uint EnableProcessedInput = 0x1;
        private const uint EnableLineInput = 0x2;
        private const uint EnableEchoInput = 0x4;
        private const uint EnableVirtualTerminalInput = 0x200;
        private const uint EnableProcessedOutput = 0x1;
        private const uint EnableVirtualTerminalProcessing = 0x4;

        private uint _inputMode;
        private uint _outputMode;
        private uint _outputCodePage;
        private bool _saved;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ReadConsoleW(IntPtr hConsoleInput, [Out] char[] lpBuffer, uint nNumberOfCharsToRead, out uint lpNumberOfCharsRead, IntPtr pInputControl);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleOutputCP();

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleOutputCP(uint wCodePageID);

        public void Enable()
        {
            var input = GetStdHandle(StdInputHandle);
            var output = GetStdHandle(StdOutputHandle);
            if (GetConsoleMode(input, out _inputMode) && GetConsoleMode(output, out _outputMode)) _saved = true;
            _outputCodePage = GetConsoleOutputCP();
            SetConsoleOutputCP(65001);
            var raw = (_inputMode & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput)) | EnableVirtualTerminalInput;
            SetConsoleMode(input, raw);
            SetConsoleMode(output, _outputMode | EnableProcessedOutput | EnableVirtualTerminalProcessing);
        }

        public void Restore()
        {
            if (!_saved) return;
            SetConsoleMode(GetStdHandle(StdInputHandle), _inputMode);
            SetConsoleMode(GetStdHandle(StdOutputHandle), _outputMode);
            if (_outputCodePage != 0) SetConsoleOutputCP(_outputCodePage);
        }

        /// <summary>Blocking read of UTF-16 console input. Returns -1 on failure.</summary>
        public static int ReadConsole(char[] buffer)
        {
            if (!ReadConsoleW(GetStdHandle(StdInputHandle), buffer, (uint)buffer.Length, out var read, IntPtr.Zero))
            {
                return Marshal.GetLastWin32Error() == 995 ? 0 : -1;
            }
            return (int)read;
        }

        public static void CancelRead() => CancelIoEx(GetStdHandle(StdInputHandle), IntPtr.Zero);
    }

    private sealed class PosixRawMode : IRawMode
    {
        private readonly byte[] _saved = new byte[256];
        private bool _hasSaved;

        [DllImport("libc", SetLastError = true)]
        private static extern int tcgetattr(int fd, byte[] termios);

        [DllImport("libc", SetLastError = true)]
        private static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

        [DllImport("libc")]
        private static extern void cfmakeraw(byte[] termios);

        public void Enable()
        {
            if (tcgetattr(0, _saved) != 0) return;
            _hasSaved = true;
            var raw = (byte[])_saved.Clone();
            cfmakeraw(raw);
            tcsetattr(0, 0, raw);
        }

        public void Restore()
        {
            if (_hasSaved) tcsetattr(0, 0, _saved);
        }
    }
}

/// <summary>Physical modifier key state (used to detect Shift+Enter on terminals that send a plain CR).</summary>
public static class NativeModifiers
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsShiftPressed()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return (GetAsyncKeyState(0x10) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }
}
