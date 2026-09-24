namespace ShareIt.Core.Models;

public sealed class TextCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Language { get; set; } = "text";
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime UpdatedAtUtc { get; set; }
}
