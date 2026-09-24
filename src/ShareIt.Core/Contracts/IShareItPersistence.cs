using ShareIt.Core.Models;

namespace ShareIt.Core.Contracts;

// Callback operations are synchronous, provider-neutral aggregate changes. The adapter
// serializes writes and commits the change and its cleanup intent in one transaction.
public interface IShareItPersistence
{
    Task<bool> TryCreateAsync(SharedSession session, int maximumSessions, CancellationToken ct = default);
    Task<SharedSession?> FindByCodeAsync(string code, CancellationToken ct = default);
    Task<SharedSession?> FindAsync(Guid id, CancellationToken ct = default);
    Task<T> MutateAsync<T>(Guid id, Func<SharedSession, long, T> change, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> FindCleanupCandidatesAsync(DateTime now, bool startup, CancellationToken ct = default);
    Task DeletePurgedAsync(Guid id, CancellationToken ct = default);
}
