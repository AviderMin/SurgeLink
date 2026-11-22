using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

/// <summary>
/// Opt-in real OpenSSH target. Configure it with SURGELINK_SSH_TEST_HOST,
/// SURGELINK_SSH_TEST_USER and SURGELINK_SSH_TEST_KEY (private key) or
/// SURGELINK_SSH_TEST_PASSWORD; SURGELINK_SSH_TEST_PORT defaults to 22.
/// Without configuration the suite is skipped instead of faking a server.
/// </summary>
public static class SshIntegrationEnvironment
{
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User) &&
        (!string.IsNullOrWhiteSpace(KeyPath) || !string.IsNullOrWhiteSpace(Password));

    public static string? Host => Environment.GetEnvironmentVariable("SURGELINK_SSH_TEST_HOST");

    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("SURGELINK_SSH_TEST_PORT"), out var port) ? port : 22;

    public static string? User => Environment.GetEnvironmentVariable("SURGELINK_SSH_TEST_USER");
    public static string? KeyPath => Environment.GetEnvironmentVariable("SURGELINK_SSH_TEST_KEY");
    public static string? KeyPassphrase => Environment.GetEnvironmentVariable("SURGELINK_SSH_TEST_KEY_PASSPHRASE");
    public static string? Password => Environment.GetEnvironmentVariable("SURGELINK_SSH_TEST_PASSWORD");
}

/// <summary>Runs only against a configured real server; otherwise reports a skip.</summary>
public sealed class SshIntegrationFactAttribute : FactAttribute
{
    public SshIntegrationFactAttribute()
    {
        if (!SshIntegrationEnvironment.IsConfigured)
        {
            Skip = "Set SURGELINK_SSH_TEST_HOST and SURGELINK_SSH_TEST_USER with SURGELINK_SSH_TEST_KEY "
                + "or SURGELINK_SSH_TEST_PASSWORD to run the real-server SSH tests.";
        }
    }
}

internal static class SshIntegration
{
    public static async Task<IProtocolSession> ConnectSessionAsync()
    {
        var key = SshIntegrationEnvironment.KeyPath;
        var request = new ConnectionRequest
        {
            Host = SshIntegrationEnvironment.Host!,
            Port = SshIntegrationEnvironment.Port,
            Username = SshIntegrationEnvironment.User!,
            Authentication = string.IsNullOrWhiteSpace(key) ? AuthenticationMethod.Password : AuthenticationMethod.PublicKey,
            PrivateKeyPath = key,
            PrivateKeyPassphrase = SshIntegrationEnvironment.KeyPassphrase,
            Password = SshIntegrationEnvironment.Password,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            KeepAliveInterval = TimeSpan.Zero,
            DisplayName = "ssh-integration",
        };
        // Trust-on-first-use into a throwaway store: the tests never touch the user's known hosts.
        var store = new JsonHostKeyStore(
            Path.Combine(Path.GetTempPath(), "surge-ssh-integration-" + Guid.NewGuid().ToString("N"), "hosts.json"));
        var context = new ProtocolHostContext(NullLoggerFactory.Instance, store,
            (_, _) => Task.FromResult(HostKeyDecision.TrustAlways));
        return await new SshProtocolPlugin().ConnectAsync(request, context, CancellationToken.None);
    }
}

/// <summary>One live connection with a private remote scratch directory.</summary>
internal sealed class SshIntegrationTarget : IAsyncDisposable
{
    private readonly IProtocolSession _session;
    private int _disposed;

    private SshIntegrationTarget(IProtocolSession session, IRemoteFileSystem files, string root)
    {
        _session = session;
        Files = files;
        Root = root;
    }

    public IRemoteFileSystem Files { get; }

    public string Root { get; }

    public static async Task<SshIntegrationTarget> ConnectAsync()
    {
        var session = await SshIntegration.ConnectSessionAsync();
        var files = session.FileSystem ?? throw new InvalidOperationException("The SSH session has no file system.");
        var home = await files.GetHomeDirectoryAsync(CancellationToken.None);
        var root = home.TrimEnd('/') + "/surge-integration-" + Guid.NewGuid().ToString("N");
        await files.CreateDirectoryAsync(root, CancellationToken.None);
        return new SshIntegrationTarget(session, files, root);
    }

    public async Task<string> CreateFileAsync(string name, string content)
    {
        var path = Root + "/" + name;
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = await Files.OpenWriteAsync(path, bytes.Length, null, CancellationToken.None);
        await stream.WriteAsync(bytes);
        return path;
    }

    public async Task<string> ReadTextAsync(string path)
    {
        await using var stream = await Files.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await Files.DeleteAsync(Root, recursive: true, cleanup.Token);
        }
        catch (Exception)
        {
            // Best effort: the session may already be gone; the scratch root name is unique.
        }
        finally
        {
            await _session.DisposeAsync();
        }
    }
}