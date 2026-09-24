namespace ShareIt.Core.Services;

// One writer per session. Purging cancels an active upload and waits for its finally
// block before deleting keys. Reference-counted entries do not outlive the work.
public sealed class SessionWorkCoordinator
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource? Active;
        public int References;
    }

    public async Task<Lease> EnterAsync(Guid id, bool cancelActive, CancellationToken ct)
    {
        var entry = Reserve(id, cancelActive);
        try { await entry.Gate.WaitAsync(ct); }
        catch { ReleaseReference(id, entry); throw; }
        return CreateLease(id, entry, ct);
    }

    // Cleanup must never queue behind an upload. Keep its durable work pending
    // and allow the worker to move on to unrelated sessions.
    public Lease? TryEnter(Guid id, bool cancelActive, CancellationToken ct)
    {
        var entry = Reserve(id, cancelActive);
        try
        {
            if (entry.Gate.Wait(0, ct)) return CreateLease(id, entry, ct);
        }
        catch { ReleaseReference(id, entry); throw; }
        ReleaseReference(id, entry);
        return null;
    }

    private Entry Reserve(Guid id, bool cancelActive)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(id, out var entry)) entries[id] = entry = new();
            entry.References++;
            if (cancelActive) entry.Active?.Cancel();
            return entry;
        }
    }

    private Lease CreateLease(Guid id, Entry entry, CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (sync) entry.Active = linked;
        return new Lease(linked.Token, () =>
        {
            lock (sync) entry.Active = null;
            linked.Dispose();
            entry.Gate.Release();
            ReleaseReference(id, entry);
        });
    }

    public void Cancel(Guid id)
    {
        lock (sync) { if (entries.TryGetValue(id, out var e)) e.Active?.Cancel(); }
    }

    private void ReleaseReference(Guid id, Entry entry)
    {
        lock (sync)
        {
            if (--entry.References == 0) { entries.Remove(id); entry.Gate.Dispose(); }
        }
    }

    public sealed class Lease(CancellationToken token, Action release) : IAsyncDisposable
    {
        public CancellationToken Token { get; } = token;
        public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
    }
}
