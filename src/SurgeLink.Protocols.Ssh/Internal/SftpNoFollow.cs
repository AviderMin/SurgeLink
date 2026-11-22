using System.Reflection;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>
/// SSH.NET 2026 resolves a path through REALPATH before LSTAT on its public surface, so
/// <c>GetAttributes</c> follows a final symbolic link. Security-sensitive callers need a
/// single-path, no-follow type check instead of a parent directory listing; the live SFTP
/// session already carries LSTAT internally. Reuse it through the one stable non-public
/// accessor, and let callers fall back to the slower listing when a future release moves it.
/// </summary>
internal static class SftpNoFollow
{
    private static readonly PropertyInfo? SessionAccessor = typeof(SftpClient)
        .GetProperty("SftpSession", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? LStatMethod = SessionAccessor is null
        ? null
        : SessionAccessor.PropertyType.GetMethod(
            "RequestLStatAsync",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(string), typeof(CancellationToken)],
            modifiers: null);

    public static bool IsAvailable => SessionAccessor is not null && LStatMethod is not null;

    /// <summary>Returns the link's own attributes, or null when the accessor is unavailable.</summary>
    public static async Task<SftpFileAttributes?> TryLStatAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        if (!IsAvailable || SessionAccessor!.GetValue(client) is not { } session)
        {
            return null;
        }

        var request = (Task<SftpFileAttributes>)LStatMethod!.Invoke(session, [path, cancellationToken])!;
        return await request.ConfigureAwait(false);
    }
}