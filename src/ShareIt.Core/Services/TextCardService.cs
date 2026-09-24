using ShareIt.Core.Configuration;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Core.Models;
using ShareIt.Core.Policies;

namespace ShareIt.Core.Services;

public sealed class TextCardService(IShareItPersistence persistence, TimeProvider clock,
    ShareItLimits limits, ISessionChangePublisher changes)
{
    public async Task<int> SaveAsync(Guid sessionId, Caller caller, int? number, Guid? version,
        string title, string content, string language, CancellationToken ct = default)
    {
        if (title.Length > 120) throw new ShareItException("title", "Keep the title under 120 characters.");
        if (language is not ("text" or "bash" or "powershell" or "json" or "yaml")) language = "text";
        var result = await persistence.MutateAsync(sessionId, (s, _) =>
        {
            SessionAccessPolicy.Require(s, caller, clock.GetUtcNow().UtcDateTime, true);
            var card = number.HasValue ? s.Texts.SingleOrDefault(x => x.Number == number) : null;
            if (number.HasValue && card == null) throw new ShareItException("conflict", "This text was deleted on another device. Your draft is still here.", 409);
            if (card != null && card.Version != version) throw new ShareItException("conflict", "This text changed on another device. Review the latest version before saving.", 409);
            QuotaPolicy.Text(s, content, card, limits);
            if (card == null)
            {
                card = new TextCard { SessionId = s.Id, Number = s.NextTextNumber++ };
                s.Texts.Add(card);
            }
            card.Title = string.IsNullOrWhiteSpace(title) ? $"Untitled text {card.Number}" : title.Trim();
            card.Content = content;
            card.Language = language;
            card.Version = Guid.NewGuid();
            card.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            s.Revision++;
            return card.Number;
        }, ct);
        changes.Publish(sessionId);
        return result;
    }

    public async Task DeleteAsync(Guid sessionId, Caller caller, int number, Guid version, CancellationToken ct = default)
    {
        await persistence.MutateAsync(sessionId, (s, _) =>
        {
            SessionAccessPolicy.Require(s, caller, clock.GetUtcNow().UtcDateTime, true);
            var card = s.Texts.SingleOrDefault(x => x.Number == number);
            if (card == null) return false;
            if (card.Version != version) throw new ShareItException("conflict", "This text has changed. Review it before deleting.", 409);
            s.Texts.Remove(card);
            s.Revision++;
            return true;
        }, ct);
        changes.Publish(sessionId);
    }
}
