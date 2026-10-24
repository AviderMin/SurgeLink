using System.Diagnostics;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class ScpRenameRegressionTests
{
    [PosixShellFact]
    public async Task RenameDoesNotOverwriteOrMoveIntoExistingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "surge-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source's file");
            var target = Path.Combine(root, "target");
            File.WriteAllText(source, "source");
            File.WriteAllText(target, "target");
            Assert.NotEqual(0, await RenameAsync(source, target));
            Assert.Equal("source", File.ReadAllText(source));
            Assert.Equal("target", File.ReadAllText(target));
            File.Delete(target);
            Directory.CreateDirectory(target);
            Assert.NotEqual(0, await RenameAsync(source, target));
            Assert.Empty(Directory.GetFileSystemEntries(target));
            Assert.True(File.Exists(source));
            Directory.Delete(target);
            Assert.Equal(0, await RenameAsync(source, target));
            Assert.False(File.Exists(source));
            Assert.Equal("source", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<int> RenameAsync(string source, string target)
    {
        static string Posix(string path) => OperatingSystem.IsWindows()
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/') : path;
        var start = new ProcessStartInfo(PosixShellFactAttribute.Shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(ScpFileSystem.BuildRenameCommand(Posix(source), Posix(target)));
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return process.ExitCode;
    }
}

public sealed class PosixShellFactAttribute : FactAttribute
{
    internal static string Shell => OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\bash.exe" : "/bin/sh";
    public PosixShellFactAttribute()
    {
        if (!File.Exists(Shell)) Skip = "A POSIX shell with GNU mv is required for the command integration test.";
    }
}
