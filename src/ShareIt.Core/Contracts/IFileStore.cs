namespace ShareIt.Core.Contracts;

public sealed record StoredFile(long Size, string Sha256);
public interface IFileStore
{
    Task<StoredFile> WriteAsync(string key, Stream source, long maximumBytes, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
