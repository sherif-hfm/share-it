using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShareIt.Core.Configuration;
using ShareIt.Core.Contracts;
using ShareIt.Core.Services;

namespace ShareIt.Infrastructure.BackgroundJobs;

public sealed class CleanupWorker(IShareItPersistence persistence, SessionPurgeService purge, ICleanupSignal signal,
    TimeProvider clock, ShareItLimits limits, ILogger<CleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ids = await persistence.FindCleanupCandidatesAsync(clock.GetUtcNow().UtcDateTime, startup, stoppingToken);
                var deferred = 0;
                await Parallel.ForEachAsync(ids, new ParallelOptions
                { MaxDegreeOfParallelism = Math.Max(1, limits.CleanupConcurrency), CancellationToken = stoppingToken }, async (id, ct) =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, limits.CleanupOperationTimeoutSeconds)));
                    try { if (!await purge.ProcessAsync(id, startup, timeout.Token)) Interlocked.Exchange(ref deferred, 1); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Session cleanup will retry. Failure type: {FailureType}", ex.GetType().Name);
                        await purge.RecordFailureAsync(id, stoppingToken);
                    }
                });
                startup = false;
                await signal.WaitAsync(TimeSpan.FromSeconds(deferred != 0 ? Math.Min(5, limits.CleanupIntervalSeconds) : limits.CleanupIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError("Cleanup cycle failed; retrying. Failure type: {FailureType}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
