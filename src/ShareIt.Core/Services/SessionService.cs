using System.Security.Cryptography;
using ShareIt.Core.Configuration;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Core.Models;
using ShareIt.Core.Policies;

namespace ShareIt.Core.Services;

public sealed class SessionService(IShareItPersistence persistence, IPinHasher hasher,
    TimeProvider clock, ShareItLimits limits, ISessionChangePublisher changes, ICleanupSignal cleanup, SessionWorkCoordinator coordinator)
{
    private readonly string dummyHash = hasher.Hash("0000");
    public async Task<CreatedSession> CreateAsync(string browserId, int minutes, CancellationToken ct = default)
    {
        if (minutes is < 1 or > 1440) throw new ShareItException("lifetime", "Choose a lifetime between 1 minute and 24 hours.");
        var now = clock.GetUtcNow().UtcDateTime;
        var pin = RandomNumberGenerator.GetInt32(10000).ToString("D4");
        var hash = hasher.Hash(pin);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
            var code = new string(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
            var s = new SharedSession { Code = code, PinHash = hash, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(minutes) };
            s.Grants.Add(new BrowserGrant { SessionId = s.Id, BrowserId = browserId });
            s.Cleanup = new CleanupTask { SessionId = s.Id, NextAttemptAtUtc = now };
            if (await persistence.TryCreateAsync(s, limits.MaxActiveSessions, ct))
                return new(s.Id, SessionCode.Format(code), pin, s.ExpiresAtUtc);
        }
        throw new ShareItException("busy", "Could not create a session. Please try again.", 503);
    }

    public async Task<Guid?> VerifyPinAsync(string code, string pin, CancellationToken ct = default)
    {
        var normalized = SessionCode.Normalize(code);
        if (normalized == "" || !SessionCode.IsPin(pin)) return null;
        var s = await persistence.FindByCodeAsync(normalized, ct);
        var valid = hasher.Verify(pin, string.IsNullOrEmpty(s?.PinHash) ? dummyHash : s.PinHash);
        if (s == null || s.Status != SessionStatus.Active || s.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime) return null;
        return valid ? s.Id : null;
    }

    public async Task JoinVerifiedAsync(Guid sessionId, string browserId, CancellationToken ct = default)
    {
        await persistence.MutateAsync(sessionId, (s, _) =>
        {
            if (s.Status != SessionStatus.Active || s.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime)
                throw new ShareItException("invalid_credentials", "The code or PIN is incorrect, or the session has ended.", 401);
            if (!s.Grants.Any(x => x.BrowserId == browserId))
            {
                if (s.Grants.Count >= 100) throw new ShareItException("participants", "This session has reached its device limit.", 429);
                s.Grants.Add(new BrowserGrant { SessionId = s.Id, BrowserId = browserId });
            }
            return true;
        }, ct);
    }

    public async Task<SessionSnapshot> GetAsync(string code, Caller caller, CancellationToken ct = default)
    {
        var s = await persistence.FindByCodeAsync(SessionCode.Normalize(code), ct)
            ?? throw new ShareItException("not_found", "This session is unavailable. Start a new session or check the code.", 404);
        SessionAccessPolicy.Require(s, caller, clock.GetUtcNow().UtcDateTime);
        return Snapshots.From(s);
    }

    public async Task ChangeExpiryAsync(Guid id, Caller caller, int hoursFromNow, CancellationToken ct = default)
    {
        if (hoursFromNow is < 1 or > 24) throw new ShareItException("lifetime", "Choose between 1 and 24 hours.");
        await persistence.MutateAsync(id, (s, _) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            SessionAccessPolicy.Require(s, caller, now, true);
            s.ExpiresAtUtc = new[] { now.AddHours(hoursFromNow), s.CreatedAtUtc.AddHours(24) }.Min();
            s.Revision++;
            return true;
        }, ct);
        changes.Publish(id);
    }

    public async Task EndAsync(Guid id, Caller caller, bool deleteDataNow, CancellationToken ct = default)
    {
        await persistence.MutateAsync(id, (s, _) =>
        {
            // Existing participants may retry an end request, but never reopen it or downgrade a purge.
            if (caller.IsReader || caller.BrowserId == null || !s.Grants.Any(x => x.BrowserId == caller.BrowserId))
                throw new ShareItException("access_denied", "Join this session to continue.", 403);
            if (s.Status == SessionStatus.Active)
            {
                s.Status = SessionStatus.Closed;
                s.ClosedAtUtc = clock.GetUtcNow().UtcDateTime;
            }
            if (deleteDataNow)
            {
                s.Cleanup.PurgeRequested = true;
                s.Cleanup.NextAttemptAtUtc = clock.GetUtcNow().UtcDateTime;
                s.Texts.Clear();
                s.PinHash = "";
            }
            s.Revision++;
            return true;
        }, ct);
        changes.Publish(id);
        coordinator.Cancel(id);
        cleanup.Wake();
    }
}
