using System.Text.Json;
using System.Diagnostics;

namespace SurgeLink.Core.Storage;

/// <summary>Reads and writes small JSON documents without corrupting them on crash.</summary>
public static class AtomicJsonFile
{
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
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var value = JsonSerializer.Deserialize<T>(stream, SerializerOptions);
            if (value is null && !tolerateErrors) throw new JsonException("The stored document is null.");
            return value ?? fallback;
        }
        catch (FileNotFoundException) { return fallback; }
        catch (DirectoryNotFoundException) { return fallback; }
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
            File.Move(temp, path, overwrite: true);
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
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var started = Stopwatch.StartNew();
        FileStream lease;
        while (true)
        {
            try { lease = new FileStream(fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
        }
        // Keep the lock file: deleting it could let another process lock a different inode.
        using (lease) return mutate();
    }
}
