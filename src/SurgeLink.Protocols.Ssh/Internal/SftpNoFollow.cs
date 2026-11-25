using System.Reflection;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>
/// SSH.NET 2026 resolves a path through REALPATH before LSTAT on its public surface, so
/// <c>GetAttributes</c> follows a final symbolic link. Security-sensitive callers need a
/// single-path, no-follow type check instead of a parent directory listing. The live SFTP
/// session already carries LSTAT and accepts literal paths for rename. If the accessor
/// changes, callers can fall back to listing for type checks. Staged renames fail closed.
/// </summary>
internal static class SftpNoFollow
{
    private static readonly PropertyInfo? SessionAccessor = typeof(SftpClient)
        .GetProperty("SftpSession", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? LStatMethod = Bind(
        "RequestLStatAsync", typeof(Task<SftpFileAttributes>), typeof(string), typeof(CancellationToken));

    private static readonly MethodInfo? RenameMethod = Bind(
        "RequestRenameAsync", typeof(Task), typeof(string), typeof(string), typeof(CancellationToken));

    private static readonly MethodInfo? PosixRenameMethod = Bind(
        "RequestPosixRename", typeof(void), typeof(string), typeof(string));

    public static bool IsAvailable => SessionAccessor is not null && LStatMethod is not null;
    internal static bool CanRename(bool overwrite) => (overwrite ? PosixRenameMethod : RenameMethod) is not null;

    /// <summary>Returns the link's own attributes, or null when the accessor is unavailable.</summary>
    public static async Task<SftpFileAttributes?> TryLStatAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        if (!IsAvailable || SessionAccessor!.GetValue(client) is not { } session)
        {
            return null;
        }

        var request = (Task<SftpFileAttributes>)Invoke(LStatMethod!, session, path, cancellationToken)!;
        return await request.ConfigureAwait(false);
    }

    public static Task RenameAsync(SftpClient client, string source, string destination, bool overwrite,
        CancellationToken cancellationToken)
    {
        var method = overwrite ? PosixRenameMethod : RenameMethod;
        if (method is null || SessionAccessor?.GetValue(client) is not { } session)
            throw new ProtocolException("This SFTP client cannot rename a staging file without resolving the destination.",
                ProtocolFailureKind.Configuration);

        if (!overwrite)
            return (Task)Invoke(method, session, source, destination, cancellationToken)!;

        // SSH.NET only exposes the synchronous OpenSSH extension.
        return Task.Run(() => { Invoke(method, session, source, destination); }, cancellationToken);
    }

    private static MethodInfo? Bind(string name, Type returnType, params Type[] parameters)
    {
        var method = SessionAccessor?.PropertyType.GetMethod(name, parameters);
        return method?.ReturnType == returnType && !method.IsStatic ? method : null;
    }

    private static object? Invoke(MethodInfo method, object session, params object?[] arguments)
    {
        try { return method.Invoke(session, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
    }
}
