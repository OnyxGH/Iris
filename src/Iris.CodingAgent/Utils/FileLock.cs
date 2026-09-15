namespace Iris.CodingAgent.Utils;

/// <summary>
/// Cross-process lock: an atomic "&lt;path&gt;.lock" directory whose
/// mtime is refreshed while held. Locks older than the stale threshold are taken over.
/// </summary>
public sealed class FileLock : IDisposable
{
    private const int StaleMs = 10_000;
    private const int UpdateMs = StaleMs / 2;

    private readonly string _lockPath;
    private readonly Timer _refresh;
    private int _released;

    private FileLock(string lockPath)
    {
        _lockPath = lockPath;
        _refresh = new Timer(_ =>
        {
            try
            {
                Directory.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow);
            }
            catch
            {
                // Best effort.
            }
        }, null, UpdateMs, UpdateMs);
    }

    public static string LockPathFor(string path) => path + ".lock";

    /// <summary>Try to acquire the lock once. Throws <see cref="IOException"/> with HResult ELOCKED semantics when held.</summary>
    public static FileLock Acquire(string path)
    {
        var lockPath = LockPathFor(path);
        if (TryCreate(lockPath)) return new FileLock(lockPath);

        try
        {
            var age = DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockPath);
            if (age.TotalMilliseconds > StaleMs)
            {
                Directory.Delete(lockPath, recursive: true);
                if (TryCreate(lockPath)) return new FileLock(lockPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        throw new LockedException($"Lock file is already being held: {lockPath}");
    }

    /// <summary>Acquire with retries (synchronous backoff).</summary>
    public static FileLock AcquireWithRetry(string path, int maxAttempts = 10, int delayMs = 20)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Acquire(path);
            }
            catch (LockedException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    public static async Task<FileLock> AcquireAsync(string path, int retries = 10, int minTimeoutMs = 100, int maxTimeoutMs = 10_000, CancellationToken cancellationToken = default)
    {
        var delay = minTimeoutMs;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Acquire(path);
            }
            catch (LockedException) when (attempt < retries)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(maxTimeoutMs, delay * 2);
            }
        }
    }

    private static bool TryCreate(string lockPath)
    {
        try
        {
            var parent = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) return false;
            if (Directory.Exists(lockPath) || File.Exists(lockPath)) return false;
            // Directory.CreateDirectory is not atomic about "already exists"; use a unique temp dir + move.
            var temp = lockPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(temp);
            try
            {
                Directory.Move(temp, lockPath);
                return true;
            }
            catch (IOException)
            {
                Directory.Delete(temp);
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1) return;
        _refresh.Dispose();
        try
        {
            Directory.Delete(_lockPath, recursive: true);
        }
        catch
        {
            // Already removed.
        }
    }
}

public sealed class LockedException(string message) : IOException(message);

/// <summary>Atomic-ish file writes.</summary>
public static class FileUtils
{
    public static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
