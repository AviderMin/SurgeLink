using System.Text;
using SurgeLink.App.Services;
using SurgeLink.Core.Files;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Tests.Fakes;
using SurgeLink.Protocols.Demo;

namespace SurgeLink.Core.Tests;

public sealed class AuditRegressionTests
{
    [Theory]
    [InlineData("sessions", "{unfinished")]
    [InlineData("credentials", "{unfinished")]
    [InlineData("hostkeys", "{unfinished")]
    [InlineData("settings", "{unfinished")]
    [InlineData("sessions", "null")]
    [InlineData("credentials", "null")]
    [InlineData("hostkeys", "null")]
    [InlineData("settings", "null")]
    public void UpdatingCorruptStorageDoesNotReplaceOriginalEvidence(string kind, string corrupt)
    {
        using var temp = new TempDirectory();
        var path = temp.File("store.json");
        File.WriteAllText(path, corrupt);
        var error = Assert.Throws<IOException>(() =>
        {
            switch (kind)
            {
                case "sessions": new JsonSessionStore(path).Upsert(SessionProfile.New("ssh", "host", 22, "user")); break;
                case "credentials": new EncryptedCredentialStore(path, new FakeSecretProtector()).Save("profile", new StoredCredential { Password = "value" }); break;
                case "hostkeys": new JsonHostKeyStore(path).Save(new KnownHostEntry { Endpoint = "host:22", Algorithm = "ssh-ed25519", FingerprintSha256 = "SHA256:value" }); break;
                case "settings": new AppSettingsStore(path).Update(s => s with { TerminalFontSize = 18 }); break;
            }
        });
        Assert.IsType<System.Text.Json.JsonException>(error.InnerException);
        Assert.Contains("store.json", error.Message, StringComparison.Ordinal);
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Fact]
    public void IndependentProfileStoresPreserveConcurrentChanges()
    {
        using var temp = new TempDirectory();
        var path = temp.File("sessions.json");
        var stores = Enumerable.Range(0, 12).Select(_ => new JsonSessionStore(path)).ToArray();
        Parallel.For(0, stores.Length, i => stores[i].Upsert(SessionProfile.New("ssh", $"host-{i}", 22, "user") with { Id = i.ToString() }));
        Assert.Equal(stores.Length, stores[0].Load().Count);
        stores[0].Delete("0");
        stores[1].Upsert(SessionProfile.New("ssh", "another", 22, "user") with { Id = "another" });
        Assert.Equal(stores.Length, stores[2].Load().Count);
        Assert.DoesNotContain(stores[2].Load(), p => p.Id == "0");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void IndependentCredentialStoresPreserveOtherProfiles()
    {
        using var temp = new TempDirectory();
        var path = temp.File("credentials.json");
        var first = new EncryptedCredentialStore(path, new FakeSecretProtector());
        var second = new EncryptedCredentialStore(path, new FakeSecretProtector());
        first.Save("one", new StoredCredential { Password = "first" });
        second.Save("two", new StoredCredential { Password = "second" });
        Assert.Equal("first", second.Get("one")?.Password);
        first.Delete("one");
        Assert.Equal("second", first.Get("two")?.Password);
        Assert.Null(second.Get("one"));
    }

    [Fact]
    public void IndependentHostKeyStoresPreserveOtherEndpoints()
    {
        using var temp = new TempDirectory();
        var path = temp.File("hosts.json");
        var first = new JsonHostKeyStore(path);
        var second = new JsonHostKeyStore(path);
        var key = new KnownHostEntry { Endpoint = "one:22", Algorithm = "ssh-ed25519", FingerprintSha256 = "SHA256:one", TrustedUtc = DateTimeOffset.UtcNow };
        first.Save(key);
        second.Save(key with { Endpoint = "two:22" });
        Assert.True(second.TryGetFingerprint("one:22", out _));
        first.Remove("one:22");
        Assert.True(first.TryGetFingerprint("two:22", out _));
        Assert.False(second.TryGetFingerprint("one:22", out _));
    }

    [Fact]
    public void IndependentSettingsUpdatesUseLatestFields()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var first = new AppSettingsStore(path);
        var second = new AppSettingsStore(path);
        first.Update(s => s with { TerminalFontSize = 18 });
        second.Update(s => s with { MonitorIntervalSeconds = 7 });
        var saved = new AppSettingsStore(path).Current;
        Assert.Equal(18, saved.TerminalFontSize);
        Assert.Equal(7, saved.MonitorIntervalSeconds);
    }

    [Fact]
    public async Task CancelledQueuedPromptNeverBecomesCurrent()
    {
        var service = new InteractionService(new AutomationDispatcher());
        var first = service.RequestTextAsync(new("first", "value"), default);
        var head = service.Current!;
        using var cancellation = new CancellationTokenSource();
        var second = service.RequestTextAsync(new("second", "value"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Same(head, service.Current);
        head.Complete("answer");
        service.Dequeue(head);
        Assert.Equal("answer", await first);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task CurrentPromptCallerCancelsBeforeNativeDialogCloses()
    {
        var service = new InteractionService(new AutomationDispatcher());
        using var cancellation = new CancellationTokenSource();
        var answer = service.RequestTextAsync(new("current", "value"), cancellation.Token);
        var head = service.Current!;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);
        Assert.Same(head, service.Current);
        Assert.False(head.Result.IsCompleted);
        head.Complete(null);
        service.Dequeue(head);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task PrecancelledPromptIsNeverEnqueued()
    {
        var service = new InteractionService(new AutomationDispatcher());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RequestTextAsync(new("cancelled", "value"), new CancellationToken(true)));
        Assert.Null(service.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteEditorPreservesEncodingAndRefreshesBaseline(bool unicode)
    {
        var files = new DemoFileSystem();
        var encoding = unicode ? (Encoding)new UnicodeEncoding(false, true, true) : new UTF8Encoding(false, true);
        const string path = "/home/demo/edit.txt";
        await WriteAsync(files, path, [.. encoding.GetPreamble(), .. encoding.GetBytes("original")]);
        var baseline = await ReadAsync(files, path);
        var next = await RemoteTextEdit.SaveAsync(files, path, baseline, "你好", 1024, default);
        var final = await RemoteTextEdit.SaveAsync(files, path, next, "second", 1024, default);
        var loaded = await ReadAsync(files, path);
        Assert.Equal("second", loaded.Text);
        Assert.Equal(final.Sha256, loaded.Sha256);
        Assert.Equal(encoding.GetPreamble(), loaded.Encoding.GetPreamble());
        Assert.DoesNotContain(await files.ListAsync("/home/demo", default), f => f.Name.StartsWith(".surge-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteEditorDoesNotOverwriteExternalChanges()
    {
        var files = new DemoFileSystem();
        const string path = "/home/demo/demo.txt";
        var baseline = await ReadAsync(files, path);
        await WriteAsync(files, path, Encoding.UTF8.GetBytes("external change"));
        await Assert.ThrowsAsync<RemoteFileConflictException>(() => RemoteTextEdit.SaveAsync(files, path, baseline, "my edit", 1024, default));
        Assert.Equal("external change", (await ReadAsync(files, path)).Text);
        Assert.DoesNotContain(await files.ListAsync("/home/demo", default), f => f.Name.StartsWith(".surge-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteEditorSerializesCompetingSavesFromSameBaseline()
    {
        var files = new DemoFileSystem();
        const string path = "/home/demo/demo.txt";
        var baseline = await ReadAsync(files, path);
        var saves = new[] { "first", "second" }.Select(text => Task.Run(() => RemoteTextEdit.SaveAsync(files, path, baseline, text, 1024, default))).ToArray();
        await Task.WhenAll(saves.Select(async save => { try { await save; } catch (RemoteFileConflictException) { } }));
        _ = Assert.Single(saves, t => t.IsCompletedSuccessfully);
        _ = Assert.Single(saves, t => t.IsFaulted && t.Exception!.InnerException is RemoteFileConflictException);
    }

    private static async Task WriteAsync(IRemoteFileSystem files, string path, byte[] bytes)
    {
        await using var output = await files.OpenWriteAsync(path, bytes.Length, null, default);
        await output.WriteAsync(bytes);
    }

    [Fact]
    public async Task DestinationIsRevalidatedAfterStagingAndTemporaryFileIsRemovedOnConflict()
    {
        var files = new DemoFileSystem();
        const string path = "/home/demo/demo.txt";
        var original = (await ReadAsync(files, path)).Text;
        var checks = 0;
        await Assert.ThrowsAsync<RemoteFileConflictException>(() => RemoteFileWrite.WriteAsync(files, path, 4, null, true,
            (output, ct) => output.WriteAsync("edit"u8.ToArray(), ct).AsTask(), default,
            _ => ++checks == 2 ? Task.FromException(new RemoteFileConflictException()) : Task.CompletedTask));
        Assert.Equal(2, checks);
        Assert.Equal(original, (await ReadAsync(files, path)).Text);
        Assert.DoesNotContain(await files.ListAsync("/home/demo", default), f => f.Name.StartsWith(".surge-", StringComparison.Ordinal));
    }

    private static async Task<TextFileDocument> ReadAsync(IRemoteFileSystem files, string path)
    {
        await using var input = await files.OpenReadAsync(path, default);
        return await BoundedTextFile.ReadDocumentAsync(input, 1024, default);
    }
}
