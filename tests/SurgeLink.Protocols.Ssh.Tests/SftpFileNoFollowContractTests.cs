using System.Reflection;
using Renci.SshNet.Sftp;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

/// <summary>
/// Locks the SSH.NET 2026.0.0 contract the SFTP no-follow fix relies on:
/// <see cref="SftpFile"/> sends the instance's own <see cref="SftpFile.FullName"/>
/// to REMOVE/RENAME, it does not canonicalize that path first.
/// </summary>
public sealed class SftpFileNoFollowContractTests
{
    [Theory]
    [InlineData(0xA1FFu, "RequestRemoveAsync")] // symbolic link
    [InlineData(0x81A4u, "RequestRemoveAsync")] // regular file
    [InlineData(0x41EDu, "RequestRmDirAsync")]  // directory
    public async Task DeleteAsyncSendsTheEntryFullNameWithoutCanonicalization(
        uint permissions,
        string expectedRequest)
    {
        var session = CreateSession();
        var file = CreateFile(session, "/home/user/entry", permissions);

        await file.DeleteAsync(CancellationToken.None);

        var call = Assert.Single(session.Calls);
        Assert.Equal(expectedRequest, call.Method);
        Assert.Equal("/home/user/entry", call.Arguments[0]);
    }

    [Fact]
    public void MoveToRenamesTheEntryFullNameWithoutCanonicalizingTheSource()
    {
        var session = CreateSession();
        session.Canonicalize = _ => "/canonical/moved";
        var file = CreateFile(session, "/home/user/link", 0xA1FFu);

        file.MoveTo("/home/user/moved");

        var rename = session.Calls.Single(call => call.Method == "RequestRename");
        Assert.Equal("/home/user/link", rename.Arguments[0]);
        Assert.Equal("/home/user/moved", rename.Arguments[1]);
        // The extra post-rename REALPATH only refreshes SftpFile's cached name.
        Assert.Equal("/canonical/moved", file.FullName);
    }

    private static RecordingSftpSessionProxy CreateSession()
    {
        var interfaceType = typeof(SftpFile).Assembly.GetType(
            "Renci.SshNet.Sftp.ISftpSession",
            throwOnError: true)!;
        var proxy = DispatchProxy.Create(interfaceType, typeof(RecordingSftpSessionProxy));
        return (RecordingSftpSessionProxy)proxy;
    }

    private static SftpFile CreateFile(
        RecordingSftpSessionProxy session,
        string fullName,
        uint permissions)
    {
        var attributesConstructor = typeof(SftpFileAttributes)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        var attributes = (SftpFileAttributes)attributesConstructor.Invoke(
            [DateTime.UnixEpoch, DateTime.UnixEpoch, 0L, 1000, 1000, permissions, new Dictionary<string, string>()]);
        var fileConstructor = typeof(SftpFile)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        return (SftpFile)fileConstructor.Invoke([session, fullName, attributes]);
    }
}

public class RecordingSftpSessionProxy : DispatchProxy
{
    public List<(string Method, object?[] Arguments)> Calls { get; } = new();

    public Func<string, string> Canonicalize { get; set; } = path => path;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        Calls.Add((targetMethod.Name, args ?? []));
        return targetMethod.Name switch
        {
            "RequestRemoveAsync" or "RequestRmDirAsync" => Task.CompletedTask,
            "RequestRename" => null,
            "GetCanonicalPath" => Canonicalize((string)args![0]!),
            _ => throw new NotSupportedException(targetMethod.Name),
        };
    }
}
