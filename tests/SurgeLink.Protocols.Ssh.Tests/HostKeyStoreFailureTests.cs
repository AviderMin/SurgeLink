using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class HostKeyStoreFailureTests
{
    [Fact]
    public void CorruptKnownHostsRejectsTrustWithoutPromptingForFirstUse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SurgeLink.HostKeyTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "hosts.json");
        try
        {
            var store = new JsonHostKeyStore(path);
            File.WriteAllText(path, "{invalid}");
            var prompts = 0;
            var verifier = new HostKeyVerifier("host.test", 22, store, (_, _) =>
            {
                prompts++;
                return Task.FromResult(new HostKeyDecision(true, false));
            }, NullLogger.Instance, default);
            using var rsa = RSA.Create(2048);
            using var key = new RsaKey(rsa.ExportRSAPrivateKey());
            var args = new HostKeyEventArgs(new KeyHostAlgorithm("ssh-rsa", key));
            verifier.Handle(null, args);
            Assert.False(args.CanTrust);
            Assert.Equal(0, prompts);
            Assert.Contains("invalid", verifier.RejectionReason);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
