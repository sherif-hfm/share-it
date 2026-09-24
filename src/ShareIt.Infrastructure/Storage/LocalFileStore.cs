using System.Buffers;
using System.Security.Cryptography;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;

namespace ShareIt.Infrastructure.Storage;

public sealed class LocalFileStore
    : IFileStore
{
    private readonly string root;
    public LocalFileStore(string directory) { root = Path.GetFullPath(directory); Directory.CreateDirectory(root); }
    private string Resolve(string key)
    {
        if (key.Length != 32 || !key.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid storage key.", nameof(key));
        return Path.Combine(root, key);
    }
    public async Task<StoredFile> WriteAsync(string key, Stream source, long maximumBytes, CancellationToken ct = default)
    {
        var target = Resolve(key);
        var temporary = target + ".upload";
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), ct);
                    if (read == 0) break;
                    size += read;
                    if (size > maximumBytes) throw new ShareItException("file_limit", "The uploaded file exceeds its allowed size.", 413);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                }
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, false);
            return new(size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
            // If this fails, the persisted Pending/DeletePending row retains the key.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Stream result = new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(result);
    }
    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = Resolve(key);
        File.Delete(path);
        File.Delete(path + ".upload");
        return Task.CompletedTask;
    }
}
