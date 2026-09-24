namespace ShareIt.Core.Configuration;

public sealed class ShareItLimits
{
    public long MaxFileBytes { get; set; } = 25_000_000;
    public long MaxSessionFileBytes { get; set; } = 100_000_000;
    public long GlobalFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaxFiles { get; set; } = 50;
    public int MaxTextCards { get; set; } = 100;
    public int MaxTextBytes { get; set; } = 64 * 1024;
    public int MaxSessionTextBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxActiveSessions { get; set; } = 1000;
    public int MaxCircuits { get; set; } = 200;
    public int CleanupIntervalSeconds { get; set; } = 60;
    public int CleanupConcurrency { get; set; } = 4;
    public int CleanupOperationTimeoutSeconds { get; set; } = 30;
}
