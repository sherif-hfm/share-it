namespace ShareIt.Core.Models;

public enum SessionStatus { Active, Closed, Purging }

public sealed class SharedSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string PinHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public SessionStatus Status { get; set; }
    public long Revision { get; set; }
    public int NextTextNumber { get; set; } = 1;
    public int NextFileNumber { get; set; } = 1;
    public List<TextCard> Texts { get; set; } = [];
    public List<FileEntry> Files { get; set; } = [];
    public List<BrowserGrant> Grants { get; set; } = [];
    public CleanupTask Cleanup { get; set; } = new();
}
