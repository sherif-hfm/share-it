using ShareIt.Core.Models;

namespace ShareIt.Core.DTOs;

public sealed record CreatedSession(Guid Id, string Code, string Pin, DateTime ExpiresAtUtc);
public sealed record SessionSnapshot(Guid Id, string Code, DateTime CreatedAtUtc, DateTime ExpiresAtUtc,
    long Revision, IReadOnlyList<TextSnapshot> Texts, IReadOnlyList<FileSnapshot> Files, long FileBytes);
public sealed record TextSnapshot(int Number, string Title, string Content, string Language, Guid Version, DateTime UpdatedAtUtc);
public sealed record FileSnapshot(int Number, string Name, long Size, string Sha256, DateTime CreatedAtUtc);
public sealed record Caller(string? BrowserId, Guid? ReadSessionId = null)
{
    public bool IsReader => ReadSessionId.HasValue;
}
public sealed class ShareItException(string code, string message, int status = 400, int? retryAfterSeconds = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public static class Snapshots
{
    public static SessionSnapshot From(SharedSession s) => new(s.Id, SessionCode.Format(s.Code), s.CreatedAtUtc,
        s.ExpiresAtUtc, s.Revision,
        s.Texts.OrderBy(x => x.Number).Select(x => new TextSnapshot(x.Number, x.Title, x.Content, x.Language, x.Version, x.UpdatedAtUtc)).ToArray(),
        s.Files.Where(x => x.State == FileState.Ready).OrderBy(x => x.Number)
            .Select(x => new FileSnapshot(x.Number, x.Name, x.Size, x.Sha256, x.CreatedAtUtc)).ToArray(),
        s.Files.Sum(x => x.Size));
}

public static class SessionCode
{
    public static string Normalize(string? input)
    {
        var value = (input ?? "").Trim().Replace("-", "").ToLowerInvariant();
        return value.Length == 6 && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9') ? value : "";
    }
    public static string Format(string code) => code.Length == 6 ? code[..3] + "-" + code[3..] : code;
    public static bool IsPin(string? pin) => pin is { Length: 4 } && pin.All(c => c is >= '0' and <= '9');
}
