using ShareIt.Core.Contracts;

namespace ShareIt.Infrastructure.BackgroundJobs;

public sealed class CleanupSignal : ICleanupSignal, IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);
    public void Wake() { try { signal.Release(); } catch (SemaphoreFullException) { } }
    public async Task WaitAsync(TimeSpan interval, CancellationToken ct) => await signal.WaitAsync(interval, ct);
    public void Dispose() => signal.Dispose();
}
