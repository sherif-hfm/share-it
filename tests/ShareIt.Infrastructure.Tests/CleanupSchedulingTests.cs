using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShareIt.Core.Contracts;
using ShareIt.Infrastructure.BackgroundJobs;
using ShareIt.Infrastructure.Persistence;

namespace ShareIt.Infrastructure.Tests;

public class CleanupSchedulingTests
{
    [Fact]
    public async Task Upgrade_repairs_old_expiry_based_schedules_without_changing_session_expiry()
    {
        await using var h = await Harness.CreateAsync("20260924152620_ExplicitApplicationKeys");
        var ordinary = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 240);
        var brokenRetry = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 480);
        var validRetry = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        foreach (var session in new[] { ordinary, brokenRetry, validRetry })
        {
            var file = await h.Files.UploadAsync(session.Id, h.Caller, "delete", 1, new MemoryStream([1]));
            await h.Files.DeleteAsync(session.Id, h.Caller, file);
            await h.Persistence.MutateAsync(session.Id, (s, _) =>
            {
                s.Cleanup.Attempts = session.Id == ordinary.Id ? 0 : 1;
                s.Cleanup.NextAttemptAtUtc = session.Id == validRetry.Id ? h.Clock.Now.UtcDateTime.AddMinutes(10) : s.ExpiresAtUtc;
                return true;
            });
        }
        await using (var db = new ShareItDbContext(new DbContextOptionsBuilder<ShareItDbContext>()
            .UseSqlite($"Data Source={Path.Combine(h.Root, "test.db")}").Options))
            await db.Database.MigrateAsync();
        h.Clock.Now = DateTimeOffset.UtcNow;
        var candidates = await h.Persistence.FindCleanupCandidatesAsync(h.Clock.Now.UtcDateTime, false);
        Assert.Contains(ordinary.Id, candidates);
        Assert.Contains(brokenRetry.Id, candidates);
        Assert.DoesNotContain(validRetry.Id, candidates);
        Assert.Equal(ordinary.ExpiresAtUtc, (await h.Persistence.FindAsync(ordinary.Id))!.ExpiresAtUtc);
        Assert.Single((await h.Persistence.FindAsync(brokenRetry.Id))!.Files);
    }

    [Fact]
    public async Task Busy_upload_does_not_block_another_sessions_purge()
    {
        await using var h = await Harness.CreateAsync();
        var busy = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        var file = await h.Files.UploadAsync(busy.Id, h.Caller, "old", 1, new MemoryStream([1]));
        await h.Files.DeleteAsync(busy.Id, h.Caller, file);
        var store = new BlockingUploadStore(h.Store); h.FileStore = store;
        using var cancelUpload = new CancellationTokenSource();
        var upload = h.Files.UploadAsync(busy.Id, h.Caller, "uploading", 1, new MemoryStream([2]), cancelUpload.Token);
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closed = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        await h.Sessions.EndAsync(closed.Id, h.Caller, true);
        var signal = new CycleSignal();
        using var worker = new CleanupWorker(h.Persistence, h.Purge, signal, h.Clock, h.Limits, NullLogger<CleanupWorker>.Instance);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await signal.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await h.Persistence.FindAsync(closed.Id));
            Assert.False(upload.IsCompleted); // Ordinary deletion must not cancel a valid upload.
            Assert.Equal(0, (await h.Persistence.FindAsync(busy.Id))!.Cleanup.Attempts);
        }
        finally
        {
            cancelUpload.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await upload);
            await worker.StopAsync(CancellationToken.None);
        }
        h.FileStore = h.Store;
        h.Clock.Now = h.Clock.Now.AddSeconds(5);
        Assert.Contains(busy.Id, await h.Persistence.FindCleanupCandidatesAsync(h.Clock.Now.UtcDateTime, false));
        await h.Purge.ProcessAsync(busy.Id, false);
        Assert.Empty((await h.Persistence.FindAsync(busy.Id))!.Files);
    }

    [Fact]
    public async Task Expiry_changes_preserve_retry_deadline_and_success_resets_backoff()
    {
        await using var h = await Harness.CreateAsync();
        var session = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        var file = await h.Files.UploadAsync(session.Id, h.Caller, "old", 1, new MemoryStream([1]));
        await h.Files.DeleteAsync(session.Id, h.Caller, file);
        await h.Purge.RecordFailureAsync(session.Id, CancellationToken.None);
        var retry = (await h.Persistence.FindAsync(session.Id))!.Cleanup.NextAttemptAtUtc;
        await h.Sessions.ChangeExpiryAsync(session.Id, h.Caller, 4);
        Assert.Equal(retry, (await h.Persistence.FindAsync(session.Id))!.Cleanup.NextAttemptAtUtc);
        Assert.Empty(await h.Persistence.FindCleanupCandidatesAsync(h.Clock.Now.UtcDateTime, false));
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        Assert.Contains(session.Id, await h.Persistence.FindCleanupCandidatesAsync(h.Clock.Now.UtcDateTime, false));
        await h.Purge.ProcessAsync(session.Id, false);
        Assert.Equal(0, (await h.Persistence.FindAsync(session.Id))!.Cleanup.Attempts);
        await h.Sessions.ChangeExpiryAsync(session.Id, h.Caller, 8);
        var next = await h.Files.UploadAsync(session.Id, h.Caller, "next", 1, new MemoryStream([2]));
        await h.Files.DeleteAsync(session.Id, h.Caller, next);
        Assert.Contains(session.Id, await h.Persistence.FindCleanupCandidatesAsync(h.Clock.Now.UtcDateTime, false));
    }

    [Fact]
    public async Task Storage_timeout_preserves_retry_keys_and_other_sessions_progress()
    {
        await using var h = await Harness.CreateAsync();
        h.Limits.CleanupOperationTimeoutSeconds = 1;
        var stalled = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        await h.Files.UploadAsync(stalled.Id, h.Caller, "stalled", 1, new MemoryStream([1]));
        var key = (await h.Persistence.FindAsync(stalled.Id))!.Files.Single().StorageKey;
        await h.Sessions.EndAsync(stalled.Id, h.Caller, true);
        var other = await h.Sessions.CreateAsync(h.Caller.BrowserId!, 60);
        await h.Sessions.EndAsync(other.Id, h.Caller, true);
        h.FileStore = new BlockingDeleteStore(h.Store, key);
        var signal = new CycleSignal();
        using var worker = new CleanupWorker(h.Persistence, h.Purge, signal, h.Clock, h.Limits, NullLogger<CleanupWorker>.Instance);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await signal.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await h.Persistence.FindAsync(other.Id));
            var retained = (await h.Persistence.FindAsync(stalled.Id))!;
            Assert.Equal(1, retained.Cleanup.Attempts);
            Assert.Equal(key, retained.Files.Single().StorageKey);
            Assert.Empty(retained.Texts);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class CycleSignal : ICleanupSignal
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Wake() { }
        public async Task WaitAsync(TimeSpan interval, CancellationToken ct)
        { Completed.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
    }
    private sealed class BlockingUploadStore(IFileStore inner) : IFileStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<StoredFile> WriteAsync(string key, Stream source, long max, CancellationToken ct = default)
        { Started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return await inner.WriteAsync(key, source, max, ct); }
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    }
    private sealed class BlockingDeleteStore(IFileStore inner, string blockedKey) : IFileStore
    {
        public Task<StoredFile> WriteAsync(string key, Stream source, long max, CancellationToken ct = default) => inner.WriteAsync(key, source, max, ct);
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
        public async Task DeleteAsync(string key, CancellationToken ct = default)
        { if (key == blockedKey) await Task.Delay(Timeout.InfiniteTimeSpan, ct); await inner.DeleteAsync(key, ct); }
    }
}
