namespace ShareIt.Core.Models;

public sealed class BrowserGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string BrowserId { get; set; } = "";
}
