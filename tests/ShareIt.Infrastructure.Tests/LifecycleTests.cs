using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Core.Models;

namespace ShareIt.Infrastructure.Tests;

public class LifecycleTests
{
    [Fact]
    public async Task Concurrent_edits_preserve_the_first_save_and_numbers_are_never_reused()
    {
        await using var h = await Harness.CreateAsync();
        var s = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        var number = await h.Texts.SaveAsync(s.Id, h.Caller, null, null, "config", "original", "text");
        var card = (await h.Sessions.GetAsync(s.Code, h.Caller)).Texts.Single();
        await h.Texts.SaveAsync(s.Id, h.Caller, number, card.Version, "config", "first update", "text");
        Assert.Equal(409, (await Assert.ThrowsAsync<ShareItException>(() => h.Texts.SaveAsync(s.Id, h.Caller, number, card.Version, "config", "stale update", "text"))).Status);
        card = (await h.Sessions.GetAsync(s.Code, h.Caller)).Texts.Single();
        Assert.Equal("first update", card.Content);
        await h.Texts.DeleteAsync(s.Id, h.Caller, number, card.Version);
        Assert.Equal(2, await h.Texts.SaveAsync(s.Id, h.Caller, null, null, "next", "next", "text"));
    }

    [Fact]
    public async Task Ending_without_deletion_blocks_access_but_retains_until_expiry()
    {
        await using var h = await Harness.CreateAsync();
        var s = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        await h.Texts.SaveAsync(s.Id, h.Caller, null, null, "note", "retained", "text");
        await h.Sessions.EndAsync(s.Id, h.Caller, false);
        Assert.Equal(410, (await Assert.ThrowsAsync<ShareItException>(() => h.Sessions.GetAsync(s.Code, h.Caller))).Status);
        Assert.Single((await h.Persistence.FindAsync(s.Id))!.Texts);
        Assert.Empty(await h.Persistence.FindCleanupCandidatesAsync(h.Clock.Now.UtcDateTime, false));
        await Assert.ThrowsAsync<ShareItException>(() => h.Sessions.ChangeExpiryAsync(s.Id, h.Caller, 4));
        h.Clock.Now = h.Clock.Now.AddHours(1);
        Assert.Contains(s.Id, await h.Persistence.FindCleanupCandidatesAsync(h.Clock.Now.UtcDateTime, false));
        await h.Purge.ProcessAsync(s.Id, false);
        Assert.Null(await h.Persistence.FindAsync(s.Id));
    }

    [Fact]
    public async Task Immediate_purge_removes_all_content_and_retry_cannot_downgrade_it()
    {
        await using var h = await Harness.CreateAsync();
        var s = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        await h.Texts.SaveAsync(s.Id, h.Caller, null, null, "secret", "sensitive", "text");
        await h.Files.UploadAsync(s.Id, h.Caller, "config.txt", 4, new MemoryStream([1, 2, 3, 4]));
        await h.Sessions.EndAsync(s.Id, h.Caller, true);
        await h.Sessions.EndAsync(s.Id, h.Caller, false);
        Assert.True((await h.Persistence.FindAsync(s.Id))!.Cleanup.PurgeRequested);
        Assert.Empty((await h.Persistence.FindAsync(s.Id))!.Texts);
        await h.Purge.ProcessAsync(s.Id, false);
        await h.Purge.ProcessAsync(s.Id, false);
        Assert.Null(await h.Persistence.FindAsync(s.Id));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Root, "files")));
    }

    [Fact]
    public async Task Failed_file_deletion_keeps_retry_keys_without_retaining_text()
    {
        await using var h = await Harness.CreateAsync();
        var s = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        await h.Texts.SaveAsync(s.Id, h.Caller, null, null, "secret", "sensitive", "text");
        await h.Files.UploadAsync(s.Id, h.Caller, "config", 1, new MemoryStream([42]));
        await h.Sessions.EndAsync(s.Id, h.Caller, true);
        h.FileStore = new FailingDeleteStore(h.Store);
        await Assert.ThrowsAsync<IOException>(() => h.Purge.ProcessAsync(s.Id, false));
        await h.Purge.RecordFailureAsync(s.Id, CancellationToken.None);
        var retained = (await h.Persistence.FindAsync(s.Id))!;
        Assert.Empty(retained.Texts); Assert.Empty(retained.Grants); Assert.Empty(retained.PinHash);
        Assert.Single(retained.Files); Assert.Equal(1, retained.Cleanup.Attempts);
        h.FileStore = h.Store;
        await h.Purge.ProcessAsync(s.Id, true);
        Assert.Null(await h.Persistence.FindAsync(s.Id));
    }

    [Fact]
    public async Task Concurrent_uploads_cannot_exceed_reserved_quota()
    {
        await using var h = await Harness.CreateAsync();
        h.Limits.MaxSessionFileBytes = 5;
        var s = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        async Task<bool> Upload()
        {
            try { await h.Files.UploadAsync(s.Id, h.Caller, "file", 4, new MemoryStream([1, 2, 3, 4])); return true; }
            catch (ShareItException ex) when (ex.Status == 413) { return false; }
        }
        var results = await Task.WhenAll(Upload(), Upload());
        Assert.Single(results, x => x);
        Assert.Equal(4, (await h.Sessions.GetAsync(s.Code, h.Caller)).FileBytes);
    }

    [Fact]
    public async Task Closing_during_an_upload_cannot_recreate_a_purged_file()
    {
        await using var h = await Harness.CreateAsync();
        var s = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        var blocking = new BlockingStore(h.Store); h.FileStore = blocking;
        var upload = h.Files.UploadAsync(s.Id, h.Caller, "in-flight", 3, new MemoryStream([1, 2, 3]));
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Sessions.EndAsync(s.Id, h.Caller, true);
        var purge = h.Purge.ProcessAsync(s.Id, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await upload);
        await purge;
        await h.Purge.ProcessAsync(s.Id, false); // A busy lease is retried after cancellation finishes.
        Assert.Null(await h.Persistence.FindAsync(s.Id));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Root, "files")));
    }

    [Fact]
    public async Task Startup_recovers_pending_uploads_and_natural_expiry()
    {
        await using var h = await Harness.CreateAsync();
        var s = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        var file = new FileEntry { SessionId = s.Id, Number = 1, Name = "partial", Size = 4, CreatedAtUtc = h.Clock.Now.UtcDateTime };
        await h.Persistence.MutateAsync(s.Id, (session, _) => { session.Files.Add(file); return true; });
        await File.WriteAllTextAsync(Path.Combine(h.Root, "files", file.StorageKey + ".upload"), "part");
        Assert.Contains(s.Id, await h.Persistence.FindCleanupCandidatesAsync(h.Clock.Now.UtcDateTime, true));
        await h.Purge.ProcessAsync(s.Id, true);
        Assert.Empty((await h.Persistence.FindAsync(s.Id))!.Files);
        h.Clock.Now = h.Clock.Now.AddHours(1);
        Assert.Equal(410, (await Assert.ThrowsAsync<ShareItException>(() => h.Sessions.GetAsync(s.Code, h.Caller))).Status);
        await h.Purge.ProcessAsync(s.Id, false);
        Assert.Null(await h.Persistence.FindAsync(s.Id));
    }

    [Fact]
    public async Task Expiry_extension_wins_over_a_stale_cleanup_candidate()
    {
        await using var h = await Harness.CreateAsync();
        var s = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        await h.Sessions.ChangeExpiryAsync(s.Id, h.Caller, 4);
        await h.Purge.ProcessAsync(s.Id, false);
        Assert.NotNull(await h.Persistence.FindAsync(s.Id));
    }

    private sealed class FailingDeleteStore(IFileStore inner) : IFileStore
    {
        public Task DeleteAsync(string key, CancellationToken ct = default) => throw new IOException("Simulated storage outage");
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
        public Task<StoredFile> WriteAsync(string key, Stream stream, long max, CancellationToken ct = default) => inner.WriteAsync(key, stream, max, ct);
    }
    private sealed class BlockingStore(IFileStore inner) : IFileStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<StoredFile> WriteAsync(string key, Stream stream, long max, CancellationToken ct = default)
        {
            Started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return await inner.WriteAsync(key, stream, max, ct);
        }
        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
    }
}
