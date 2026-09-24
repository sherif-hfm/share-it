using ShareIt.Core.Contracts;
using ShareIt.Core.Models;

namespace ShareIt.Core.Services;

public sealed class SessionPurgeService(IShareItPersistence persistence, IFileStore files,
    TimeProvider clock, SessionWorkCoordinator coordinator, ISessionChangePublisher changes)
{
    public async Task<bool> ProcessAsync(Guid id, bool startup, CancellationToken ct = default)
    {
        var existing = await persistence.FindAsync(id, ct);
        if (existing == null) return true;
        var purge = existing.Cleanup.PurgeRequested || existing.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime || existing.Status == SessionStatus.Purging;
        if (purge)
        {
            purge = await persistence.MutateAsync(id, (s, _) =>
            {
                // An expiry extension can race the candidate scan: recheck inside the transaction.
                if (!s.Cleanup.PurgeRequested && s.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime && s.Status != SessionStatus.Purging) return false;
                s.Status = SessionStatus.Purging;
                s.Cleanup.PurgeRequested = true;
                s.Texts.Clear(); s.Grants.Clear(); s.PinHash = "";
                s.Revision++;
                return true;
            }, ct);
            changes.Publish(id);
        }
        await using var lease = coordinator.TryEnter(id, purge, ct);
        if (lease == null)
        {
            await persistence.MutateAsync(id, (s, _) =>
            {
                s.Cleanup.NextAttemptAtUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(5);
                return true;
            }, ct);
            return false;
        }
        existing = await persistence.FindAsync(id, ct);
        if (existing == null) return true;
        purge = existing.Status == SessionStatus.Purging;
        var targets = existing.Files.Where(f => purge || f.State == FileState.DeletePending ||
            (f.State == FileState.Pending && (startup || f.CreatedAtUtc < clock.GetUtcNow().UtcDateTime.AddHours(-1)))).ToArray();
        foreach (var file in targets)
        {
            await files.DeleteAsync(file.StorageKey, ct);
            await persistence.MutateAsync(id, (s, _) =>
            {
                s.Files.RemoveAll(f => f.Id == file.Id);
                return true;
            }, ct);
        }
        if (purge) await persistence.DeletePurgedAsync(id, ct);
        else await persistence.MutateAsync(id, (s, _) =>
        {
            s.Cleanup.Attempts = 0;
            s.Cleanup.NextAttemptAtUtc = clock.GetUtcNow().UtcDateTime;
            return true;
        }, ct);
        changes.Publish(id);
        return true;
    }

    public async Task RecordFailureAsync(Guid id, CancellationToken ct)
    {
        if (await persistence.FindAsync(id, ct) == null) return;
        await persistence.MutateAsync(id, (s, _) =>
        {
            s.Cleanup.Attempts++;
            s.Cleanup.NextAttemptAtUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(7, s.Cleanup.Attempts))));
            return true;
        }, ct);
    }
}
