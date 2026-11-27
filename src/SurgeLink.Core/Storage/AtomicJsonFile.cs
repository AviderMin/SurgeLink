using System.Diagnostics;
using System.Text.Json;

namespace SurgeLink.Core.Storage;

/// <summary>Reads and writes small JSON documents without corrupting them on crash.</summary>
public static class AtomicJsonFile
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private static readonly StringComparer LockPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [ThreadStatic]
    private static HashSet<string>? _heldLocks;

    /// <summary>Default serializer settings shared by every store.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a document. Mutations must disable error tolerance to avoid overwriting unreadable data.</summary>
    public static T Read<T>(string path, T fallback, bool tolerateErrors = true)
    {
        try
        {
            using var readLease = AcquireSharedReadLease(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var value = JsonSerializer.Deserialize<T>(stream, SerializerOptions);
            if (value is null && !tolerateErrors) throw new JsonException("The stored document is null.");
            return value ?? fallback;
        }
        catch (FileNotFoundException) { return fallback; }
        catch (DirectoryNotFoundException) { return fallback; }
        catch (JsonException ex) when (!tolerateErrors)
        {
            throw new IOException($"Stored JSON file '{Path.GetFileName(path)}' is invalid. Restore or repair the file before saving.", ex);
        }
        catch (Exception ex) when (tolerateErrors && (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException))
        {
            return fallback;
        }
    }

    /// <summary>Writes <paramref name="value"/> to a temp file then swaps it in.</summary>
    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }
            MoveWithBriefRetry(temp, path);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Coordinates synchronous read/modify/write across store instances and desktop processes.</summary>
    public static TResult WithExclusiveAccess<TResult>(string path, Func<TResult> mutate)
    {
        var fullPath = Path.GetFullPath(path);
        var held = _heldLocks ??= new HashSet<string>(LockPathComparer);
        if (!held.Add(fullPath))
        {
            return mutate();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var started = Stopwatch.StartNew();
            FileStream lease;
            while (true)
            {
                try { lease = new FileStream(fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
                catch (IOException ex) when (IsLockContention(ex) && started.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
            }
            // Keep the lock file: deleting it could let another process lock a different inode.
            using (lease) return mutate();
        }
        finally
        {
            held.Remove(fullPath);
        }
    }

    private static FileStream? AcquireSharedReadLease(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return null; }

        if (_heldLocks?.Contains(fullPath) == true)
        {
            return null;
        }

        var lockPath = fullPath + ".lock";
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException ex) when (IsLockContention(ex) && started.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void MoveWithBriefRetry(string source, string destination)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (started.Elapsed < TimeSpan.FromMilliseconds(120)
                && (ex is UnauthorizedAccessException || (ex is IOException io && IsLockContention(io))))
            {
                Thread.Sleep(15);
            }
        }
    }

    private static bool IsLockContention(IOException exception) =>
        !OperatingSystem.IsWindows()
        || exception.HResult is ErrorSharingViolation or ErrorLockViolation;
}
