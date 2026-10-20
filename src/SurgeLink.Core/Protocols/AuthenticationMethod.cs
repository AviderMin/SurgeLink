namespace SurgeLink.Core.Protocols;

/// <summary>How a session authenticates against the remote endpoint.</summary>
public enum AuthenticationMethod
{
    /// <summary>Plain password supplied by the user or a credential store.</summary>
    Password,

    /// <summary>OpenSSH style private key file, optionally passphrase protected.</summary>
    PublicKey,

    /// <summary>Interactive prompt driven authentication (2FA, OTP, TOTP).</summary>
    KeyboardInteractive,
}
