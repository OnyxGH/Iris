using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Ai.Utils;

public static class TextUtils
{
    /// <summary>Removes unpaired UTF-16 surrogate characters (they break JSON serialization at many providers).</summary>
    public static string SanitizeSurrogates(string text)
    {
        var needsWork = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogate(text[i]))
            {
                needsWork = true;
                break;
            }
        }
        if (!needsWork) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Fast deterministic hash to shorten long strings (cyrb53-style, identical to pi-ai shortHash).</summary>
    public static string ShortHash(string str)
    {
        uint h1 = 0xdeadbeef;
        uint h2 = 0x41c6ce57;
        foreach (var ch in str)
        {
            h1 = unchecked((h1 ^ ch) * 2654435761u);
            h2 = unchecked((h2 ^ ch) * 1597334677u);
        }
        h1 = unchecked(((h1 ^ (h1 >> 16)) * 2246822507u) ^ ((h2 ^ (h2 >> 13)) * 3266489909u));
        h2 = unchecked(((h2 ^ (h2 >> 16)) * 2246822507u) ^ ((h1 ^ (h1 >> 13)) * 3266489909u));
        return ToBase36(h2) + ToBase36(h1);
    }

    public static string ToBase36(ulong value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0) return "0";
        Span<char> buffer = stackalloc char[16];
        var pos = buffer.Length;
        while (value > 0)
        {
            buffer[--pos] = digits[(int)(value % 36)];
            value /= 36;
        }
        return new string(buffer[pos..]);
    }

    /// <summary>Extract and join text from message content blocks.</summary>
    public static string ContentText(IEnumerable<ContentBlock> content, string separator = "\n") =>
        string.Join(separator, content.OfType<TextContent>().Select(b => b.Text));

    public static string ContentText(UserContent content, string separator = "\n") =>
        content.Text ?? ContentText(content.Blocks ?? [], separator);

    /// <summary>JS-style String.prototype.slice on UTF-16 code units, clamped.</summary>
    public static string Slice(this string s, int start, int? end = null)
    {
        var len = s.Length;
        var from = start < 0 ? Math.Max(len + start, 0) : Math.Min(start, len);
        var e = end ?? len;
        var to = e < 0 ? Math.Max(len + e, 0) : Math.Min(e, len);
        return to <= from ? "" : s.Substring(from, to - from);
    }
}

/// <summary>Time-ordered UUIDv7 generator, equivalent to pi-ai uuidv7().</summary>
public static class UuidV7
{
    private const long MaxTimestamp = 0xffffffffffff;
    private static readonly ulong MaxSequence = (1UL << 41) - 1;
    private static readonly object Gate = new();
    private static long _lastOrdinaryTimestamp = -1;
    private static ulong? _sequence;

    public static string New(long? timestampMs = null)
    {
        var requested = timestampMs ?? TimeUtil.NowMs();
        if (requested < 0 || requested > MaxTimestamp)
            throw new ArgumentOutOfRangeException(nameof(timestampMs), $"UUIDv7 timestamp must be an integer between 0 and {MaxTimestamp}");

        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        ulong sequence;
        long effective;
        lock (Gate)
        {
            effective = timestampMs is null ? Math.Max(requested, _lastOrdinaryTimestamp) : timestampMs.Value;
            if (timestampMs is null) _lastOrdinaryTimestamp = effective;

            if (_sequence is null)
            {
                _sequence = ((ulong)bytes[1] << 32) | ((ulong)bytes[2] << 24) | ((ulong)bytes[3] << 16) | ((ulong)bytes[4] << 8) | bytes[5];
            }
            else
            {
                if (_sequence == MaxSequence) throw new InvalidOperationException("UUIDv7 generator sequence exhausted");
                _sequence++;
            }
            sequence = _sequence.Value;
        }

        var ts = (ulong)effective;
        for (var index = 5; index >= 0; index--)
        {
            bytes[index] = (byte)((ts >> ((5 - index) * 8)) & 0xff);
        }
        bytes[6] = (byte)(0x70 | (int)((sequence >> 37) & 0x0f));
        bytes[7] = (byte)((sequence >> 29) & 0xff);
        bytes[8] = (byte)(0x80 | (int)((sequence >> 23) & 0x3f));
        bytes[9] = (byte)((sequence >> 15) & 0xff);
        bytes[10] = (byte)((sequence >> 7) & 0xff);
        bytes[11] = (byte)(((int)((sequence & 0x7f) << 1)) | (bytes[11] & 0x01));

        var hex = Convert.ToHexStringLower(bytes);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}

public static class ProviderEnv
{
    /// <summary>Resolve a provider env value from scoped overrides then the process environment.</summary>
    public static string? Get(string name, IReadOnlyDictionary<string, string>? env = null)
    {
        if (env is not null && env.TryGetValue(name, out var scoped) && !string.IsNullOrEmpty(scoped)) return scoped;
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

public static class PiUserAgent
{
    private static readonly Lazy<string> Value = new(() =>
    {
        var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "ia32",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"pi ({platform} {Environment.OSVersion.Version}; {arch})";
    });

    public static string Get() => Value.Value;
}
