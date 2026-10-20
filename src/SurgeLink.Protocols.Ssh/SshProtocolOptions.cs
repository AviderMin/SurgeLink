using System.Globalization;
using System.Text;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// Typed accessors for the SSH specific keys of <see cref="ConnectionRequest.Options"/>.
/// Unknown keys are ignored so a profile can carry options for several protocols.
/// </summary>
public sealed class SshProtocolOptions
{
    /// <summary>Option key selecting the TERM value sent to the server.</summary>
    public const string TerminalTypeKey = "terminalType";

    /// <summary>Alias of <see cref="TerminalTypeKey"/>.</summary>
    public const string TerminalTypeAliasKey = "term";

    /// <summary>Option key selecting the encoding used to decode command output.</summary>
    public const string EncodingKey = "encoding";

    /// <summary>Option key enabling legacy (SHA-1, CBC, 3DES) algorithms.</summary>
    public const string UseLegacyAlgorithmsKey = "useLegacyAlgorithms";

    /// <summary>Alias of <see cref="UseLegacyAlgorithmsKey"/>.</summary>
    public const string LegacyAlgorithmsAliasKey = "legacyAlgorithms";

    /// <summary>Option key preferring one key exchange algorithm by name.</summary>
    public const string PreferredKeyExchangeKey = "preferredKeyExchange";

    /// <summary>Alias of <see cref="PreferredKeyExchangeKey"/>.</summary>
    public const string PreferredKeyExchangeAliasKey = "kex";

    /// <summary>Option key overriding the shell read buffer size in bytes.</summary>
    public const string ShellBufferSizeKey = "shellBufferSize";

    /// <summary>Option key overriding the timeout of remote commands, in seconds.</summary>
    public const string CommandTimeoutSecondsKey = "commandTimeoutSeconds";

    /// <summary>Default TERM value.</summary>
    public const string DefaultTerminalType = "xterm-256color";

    private const int DefaultShellBufferSize = 4096;
    private const int MinimumShellBufferSize = 256;
    private const int MaximumShellBufferSize = 64 * 1024;
    private const double DefaultCommandTimeoutSeconds = 20d;
    private const double MaximumCommandTimeoutSeconds = 300d;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumConnectTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Creates the option view for one connection request.</summary>
    /// <param name="request">The request whose <see cref="ConnectionRequest.Options"/> are read.</param>
    public SshProtocolOptions(ConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;

        TerminalType = FirstNonEmpty(TerminalTypeKey, TerminalTypeAliasKey) ?? DefaultTerminalType;
        EncodingName = request.GetOption(EncodingKey, "utf-8");
        Encoding = ResolveEncoding(EncodingName);
        UseLegacyAlgorithms = request.GetFlag(UseLegacyAlgorithmsKey) || request.GetFlag(LegacyAlgorithmsAliasKey);
        PreferredKeyExchange = FirstNonEmpty(PreferredKeyExchangeKey, PreferredKeyExchangeAliasKey);
        ShellBufferSize = Math.Clamp(
            ParseInt(request.GetOption(ShellBufferSizeKey, string.Empty), DefaultShellBufferSize),
            MinimumShellBufferSize,
            MaximumShellBufferSize);
        CommandTimeout = ClampSeconds(
            ParseDouble(request.GetOption(CommandTimeoutSecondsKey, string.Empty), DefaultCommandTimeoutSeconds));
        ConnectTimeout = ClampConnectTimeout(request.ConnectTimeout);
    }

    /// <summary>The request this view was created from.</summary>
    public ConnectionRequest Request { get; }

    /// <summary>TERM value requested for the interactive shell.</summary>
    public string TerminalType { get; }

    /// <summary>Encoding name as written in the options.</summary>
    public string EncodingName { get; }

    /// <summary>Encoding used to decode command output.</summary>
    public Encoding Encoding { get; }

    /// <summary>True when legacy transport algorithms should be preferred.</summary>
    public bool UseLegacyAlgorithms { get; }

    /// <summary>Key exchange algorithm to prefer, when the user selected one.</summary>
    public string? PreferredKeyExchange { get; }

    /// <summary>Buffer size used by the shell read loop.</summary>
    public int ShellBufferSize { get; }

    /// <summary>Timeout applied to non interactive remote commands.</summary>
    public TimeSpan CommandTimeout { get; }

    /// <summary>Connection timeout, clamped to a sane range.</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>
    /// Resolves an encoding name to an <see cref="Encoding"/> instance. Unknown or
    /// unavailable names fall back to UTF-8 because the shell channel itself is byte based.
    /// </summary>
    /// <param name="name">Encoding name from the options; may be <c>null</c> or empty.</param>
    public static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Encoding.UTF8;
        }

        var normalized = name.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "utf-8":
            case "utf8":
                return Encoding.UTF8;
            case "ascii":
            case "us-ascii":
                return Encoding.ASCII;
            case "latin1":
            case "latin-1":
            case "iso-8859-1":
                return Encoding.Latin1;
            default:
                return TryGetEncoding(normalized);
        }

        static Encoding TryGetEncoding(string encodingName)
        {
            try
            {
                return Encoding.GetEncoding(encodingName);
            }
            catch (ArgumentException)
            {
                return Encoding.UTF8;
            }
            catch (NotSupportedException)
            {
                return Encoding.UTF8;
            }
        }
    }

    private string? FirstNonEmpty(string key, string alias)
    {
        var value = Request.GetOption(key, string.Empty);
        return string.IsNullOrWhiteSpace(value) ? NullIfBlank(Request.GetOption(alias, string.Empty)) : value.Trim();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static TimeSpan ClampSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return TimeSpan.FromSeconds(DefaultCommandTimeoutSeconds);
        }

        return TimeSpan.FromSeconds(Math.Min(seconds, MaximumCommandTimeoutSeconds));
    }

    private static TimeSpan ClampConnectTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return DefaultConnectTimeout;
        }

        return timeout > MaximumConnectTimeout ? MaximumConnectTimeout : timeout;
    }
}
