namespace SurgeLink.Core.Tests;

/// <summary>Creates a unique temporary directory and removes it on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "surge-link-core-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Absolute path of the directory.</summary>
    public string Root { get; }

    /// <summary>Builds a path inside the temporary directory.</summary>
    public string File(string fileName) => Path.Combine(Root, fileName);

    /// <summary>Builds a nested path inside the temporary directory.</summary>
    public string PathOf(params string[] segments) => Path.Combine(new[] { Root }.Concat(segments).ToArray());

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup; a locked file must not fail a test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
