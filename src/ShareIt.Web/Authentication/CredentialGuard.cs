using ShareIt.Core.DTOs;
using ShareIt.Core.Services;

namespace ShareIt.Web.Authentication;

public sealed class CredentialGuard(SessionService sessions, TimeProvider clock) : IDisposable
{
    private sealed record Counter(DateTime Until, int Count);
    private readonly Dictionary<string, Counter> failures = [];
    private readonly object sync = new();
    private readonly SemaphoreSlim verificationSlots = new(4, 4);

    public async Task<Guid> VerifyAsync(string code, string pin, string source, CancellationToken ct)
    {
        var normalized = SessionCode.Normalize(code);
        if (normalized.Length == 0 || !SessionCode.IsPin(pin)) throw Invalid();
        var sourceKey = $"ip:{source}:{normalized}";
        var sessionKey = $"session:{normalized}";
        await verificationSlots.WaitAsync(ct);
        try
        {
            lock (sync)
            {
                var now = clock.GetUtcNow().UtcDateTime;
                if (failures.Count > 2000)
                    foreach (var key in failures.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) failures.Remove(key);
                if (failures.Count >= 20000) throw Throttled(failures.Values.Min(x => x.Until), now);
                var until = new[] { BlockedUntil(sourceKey, 5, now), BlockedUntil(sessionKey, 20, now) }.Max();
                if (until.HasValue) throw Throttled(until.Value, now);
            }
            var id = await sessions.VerifyPinAsync(normalized, pin, ct);
            if (id.HasValue) return id.Value;
            lock (sync)
            {
                Add(sourceKey, TimeSpan.FromMinutes(5));
                Add(sessionKey, TimeSpan.FromHours(1));
            }
            throw Invalid();
        }
        finally { verificationSlots.Release(); }
    }
    private DateTime? BlockedUntil(string key, int max, DateTime now) =>
        failures.TryGetValue(key, out var x) && x.Until > now && x.Count >= max ? x.Until : null;
    private void Add(string key, TimeSpan window)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        failures[key] = failures.TryGetValue(key, out var previous) && previous.Until > now
            ? previous with { Count = previous.Count + 1 } : new(now.Add(window), 1);
    }
    private static ShareItException Invalid() => new("invalid_credentials", "The code or PIN is incorrect, or the session has ended.", 401);
    private static ShareItException Throttled(DateTime until, DateTime now) => new("try_later",
        "Too many incorrect attempts. Please try again later.", 429, Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds)));
    public void Dispose() => verificationSlots.Dispose();
}
