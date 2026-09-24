using Microsoft.EntityFrameworkCore;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Core.Models;

namespace ShareIt.Infrastructure.Persistence;

public sealed class EfShareItPersistence(IDbContextFactory<ShareItDbContext> factory) : IShareItPersistence, IDisposable
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private static IQueryable<SharedSession> Full(ShareItDbContext db) => db.Sessions
        .Include(s => s.Texts).Include(s => s.Files).Include(s => s.Grants).Include(s => s.Cleanup).AsSplitQuery();

    public async Task<bool> TryCreateAsync(SharedSession session, int maximumSessions, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.Sessions.CountAsync(ct) >= maximumSessions)
                throw new ShareItException("capacity", "All session slots are currently in use. Please try again shortly.", 503);
            if (await db.Sessions.AnyAsync(s => s.Code == session.Code, ct)) return false;
            db.Add(session);
            await db.SaveChangesAsync(ct);
            return true;
        }
        finally { writes.Release(); }
    }

    public async Task<SharedSession?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Full(db).AsNoTracking().SingleOrDefaultAsync(s => s.Code == code, ct);
    }
    public async Task<SharedSession?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Full(db).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
    }
    public async Task<T> MutateAsync<T>(Guid id, Func<SharedSession, long, T> change, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var session = await Full(db).SingleOrDefaultAsync(s => s.Id == id, ct)
                ?? throw new ShareItException("not_found", "This session is no longer available.", 404);
            var global = await db.Files.SumAsync(f => (long?)f.Size, ct) ?? 0;
            var result = change(session, global);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        finally { writes.Release(); }
    }
    public async Task<IReadOnlyList<Guid>> FindCleanupCandidatesAsync(DateTime now, bool startup, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stale = now.AddHours(-1);
        return await db.Sessions.Where(s => s.Cleanup.NextAttemptAtUtc <= now &&
            (s.Cleanup.PurgeRequested || s.ExpiresAtUtc <= now || s.Files.Any(f => f.State == FileState.DeletePending ||
                (f.State == FileState.Pending && (startup || f.CreatedAtUtc < stale)))))
            .OrderByDescending(s => s.Cleanup.PurgeRequested || s.ExpiresAtUtc <= now)
            .ThenBy(s => s.Cleanup.NextAttemptAtUtc).Select(s => s.Id).Take(100).ToArrayAsync(ct);
    }
    public async Task DeletePurgedAsync(Guid id, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var s = await db.Sessions.Include(x => x.Files).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is { Status: SessionStatus.Purging } && s.Files.Count == 0)
            {
                db.Remove(s);
                await db.SaveChangesAsync(ct);
            }
        }
        finally { writes.Release(); }
    }
    public void Dispose() => writes.Dispose();
}
