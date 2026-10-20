using Microsoft.Extensions.Logging;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Protocols.Demo;

/// <summary>
/// A fully offline protocol used to exercise the UI without a server, and as the
/// reference implementation for <em>adding a protocol</em>.
/// </summary>
public sealed class DemoProtocolPlugin : IProtocolPlugin
{
    /// <inheritdoc />
    public ProtocolDescriptor Descriptor { get; } = new(
        "demo",
        "Demo (offline)",
        0,
        SessionCapabilities.Terminal | SessionCapabilities.FileSystem | SessionCapabilities.Monitor)
    {
        Glyph = "\uE9D9",
        Description = "Local sandbox session: fake shell, in-memory files, synthetic metrics.",
    };

    /// <inheritdoc />
    public async Task<IProtocolSession> ConnectAsync(
        ConnectionRequest request,
        ProtocolHostContext context,
        CancellationToken cancellationToken)
    {
        await Task.Delay(350, cancellationToken).ConfigureAwait(false);

        if (string.Equals(request.Host, "fail", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProtocolException(
                "The demo protocol cannot connect to host 'fail'. Pick any other host name.",
                ProtocolFailureKind.Network);
        }

        var logger = context.CreateLogger<DemoSession>();
        return new DemoSession(request, logger);
    }
}
