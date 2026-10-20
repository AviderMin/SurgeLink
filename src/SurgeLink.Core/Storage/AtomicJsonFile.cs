using System.Text.Json;

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

    /// <summary>Reads <paramref name="path"/>, returning <paramref name="fallback"/> on any failure.</summary>
    public static T Read<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, SerializerOptions) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
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

        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, value, SerializerOptions);
        }

        File.Move(temp, path, overwrite: true);
    }
}
