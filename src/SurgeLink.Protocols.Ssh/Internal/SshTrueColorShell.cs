using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Requests COLORTERM on the PTY channel before starting the shell.</summary>
internal static class SshTrueColorShell
{
    // SSH.NET 2026 initializes the stream and subscribes to channel events in
    // this constructor. The public factory opens the shell before returning.
    private const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly PropertyInfo? Session = typeof(BaseClient).GetProperty("Session", NonPublicInstance);
    private static readonly FieldInfo? Channel = typeof(ShellStream).GetField("_channel", NonPublicInstance);
    private static readonly ConstructorInfo? CreateStream = Session is null ? null
        : typeof(ShellStream).GetConstructor(NonPublicInstance, null, [Session.PropertyType, typeof(int), typeof(bool)], null);
    private static readonly MethodInfo? Open = Bind("Open", typeof(void));
    private static readonly MethodInfo? RequestPty = Bind("SendPseudoTerminalRequest", typeof(bool),
        typeof(string), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(IDictionary<TerminalModes, uint>));
    private static readonly MethodInfo? RequestEnvironment = Bind("SendEnvironmentVariableRequest", typeof(bool), typeof(string), typeof(string));
    private static readonly MethodInfo? RequestShell = Bind("SendShellRequest", typeof(bool));

    internal static bool IsAvailable => CreateStream is not null && Open is not null && RequestPty is not null
        && RequestEnvironment is not null && RequestShell is not null;

    internal static bool AdvertisesTrueColor(string terminalType) =>
        terminalType.StartsWith("xterm", StringComparison.Ordinal)
        || terminalType.EndsWith("-256color", StringComparison.Ordinal)
        || terminalType.EndsWith("-direct", StringComparison.Ordinal);

    internal static ShellStream Create(SshClient client, string terminalType, uint columns, uint rows, int bufferSize, ILogger logger)
    {
        if (!AdvertisesTrueColor(terminalType) || !IsAvailable || Session!.GetValue(client) is not { } session)
            return client.CreateShellStream(terminalType, columns, rows, 0, 0, bufferSize);

        ShellStream? stream = null;
        try
        {
            stream = (ShellStream)CreateStream!.Invoke([session, bufferSize, false]);
            var channel = Channel!.GetValue(stream);
            Open!.Invoke(channel, null);
            if (RequestPty!.Invoke(channel, [terminalType, columns, rows, 0u, 0u, new Dictionary<TerminalModes, uint>()]) is not true)
                throw new SshException("The server refused the pseudo-terminal request.");
            if (RequestEnvironment!.Invoke(channel, ["COLORTERM", "truecolor"]) is not true)
                logger.LogDebug("The server declined the SSH COLORTERM request.");
            if (RequestShell!.Invoke(channel, null) is not true)
                throw new SshException("The server refused the shell request.");
            return stream;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            stream?.Dispose();
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static MethodInfo? Bind(string name, Type returnType, params Type[] args)
    {
        var channelType = Channel?.FieldType;
        if (channelType is null) { return null; }
        var method = channelType.GetMethod(name, args)
            ?? channelType.GetInterfaces().Select(type => type.GetMethod(name, args)).FirstOrDefault(info => info is not null);
        return method?.ReturnType == returnType ? method : null;
    }
}
