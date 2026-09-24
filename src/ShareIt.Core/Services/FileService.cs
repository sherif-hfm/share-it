using ShareIt.Core.Configuration;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Core.Models;
using ShareIt.Core.Policies;

namespace ShareIt.Core.Services;

public sealed class FileService(IShareItPersistence persistence, IFileStore store, TimeProvider clock,
    ShareItLimits limits, ISessionChangePublisher changes, ICleanupSignal cleanup, SessionWorkCoordinator coordinator)
{
    public async Task<int> UploadAsync(Guid sessionId, Caller caller, string fileName, long size, Stream source, CancellationToken ct = default)
    {
        var name = CleanName(fileName);
        await using var lease = await coordinator.EnterAsync(sessionId, false, ct);
        var file = await persistence.MutateAsync(sessionId, (s, global) =>
        {
            SessionAccessPolicy.Require(s, caller, clock.GetUtcNow().UtcDateTime, true);
            QuotaPolicy.File(s, size, global, limits);
            var entry = new FileEntry { SessionId = s.Id, Name = name, Number = s.NextFileNumber++, Size = size, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
            s.Files.Add(entry);
            return entry;
        }, lease.Token);
        try
        {
            var uploaded = await store.WriteAsync(file.StorageKey, source, Math.Min(size, limits.MaxFileBytes), lease.Token);
            if (uploaded.Size != size) throw new ShareItException("upload_size", "The upload was incomplete. Please try again.");
            await persistence.MutateAsync(sessionId, (s, _) =>
            {
                SessionAccessPolicy.Require(s, caller, clock.GetUtcNow().UtcDateTime, true);
                var entry = s.Files.Single(x => x.Id == file.Id);
                entry.State = FileState.Ready;
                entry.Sha256 = uploaded.Sha256;
                s.Revision++;
                return true;
            }, lease.Token);
            changes.Publish(sessionId);
            return file.Number;
        }
        catch
        {
            // Persist retry intent before trying physical deletion. The purge worker
            // waits for this lease before forgetting any file keys.
            await persistence.MutateAsync(sessionId, (s, _) =>
            {
                var entry = s.Files.SingleOrDefault(x => x.Id == file.Id);
                if (entry != null) entry.State = FileState.DeletePending;
                return true;
            }, CancellationToken.None);
            cleanup.Wake();
            throw;
        }
    }

    public async Task<(Stream Stream, string Name)> DownloadAsync(Guid sessionId, Caller caller, int number, CancellationToken ct = default)
    {
        var s = await persistence.FindAsync(sessionId, ct) ?? throw new ShareItException("not_found", "This file is no longer available.", 404);
        SessionAccessPolicy.Require(s, caller, clock.GetUtcNow().UtcDateTime);
        var file = s.Files.SingleOrDefault(x => x.Number == number && x.State == FileState.Ready)
            ?? throw new ShareItException("not_found", "This file is no longer available.", 404);
        try { return (await store.OpenReadAsync(file.StorageKey, ct), file.Name); }
        catch (FileNotFoundException) { throw new ShareItException("not_found", "This file is no longer available.", 404); }
    }

    public async Task DeleteAsync(Guid sessionId, Caller caller, int number, CancellationToken ct = default)
    {
        await persistence.MutateAsync(sessionId, (s, _) =>
        {
            SessionAccessPolicy.Require(s, caller, clock.GetUtcNow().UtcDateTime, true);
            var file = s.Files.SingleOrDefault(x => x.Number == number);
            if (file != null) file.State = FileState.DeletePending;
            s.Revision++;
            return true;
        }, ct);
        changes.Publish(sessionId);
        cleanup.Wake();
    }

    public static string CleanName(string value)
    {
        var name = value.Replace('\\', '/').Split('/').Last();
        name = new string(name.Where(c => !char.IsControl(c) && c is not ':' and not '"').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 180)
            throw new ShareItException("filename", "Use a filename between 1 and 180 characters.");
        return name;
    }
}
