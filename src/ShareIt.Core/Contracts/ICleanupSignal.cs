namespace ShareIt.Core.Contracts;

public interface ICleanupSignal
{
    void Wake();
    Task WaitAsync(TimeSpan interval, CancellationToken ct);
}
