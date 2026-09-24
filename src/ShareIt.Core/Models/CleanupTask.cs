namespace ShareIt.Core.Models;

public sealed class CleanupTask
{
    public Guid SessionId { get; set; }
    public bool PurgeRequested { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public int Attempts { get; set; }
}
