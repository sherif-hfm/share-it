namespace ShareIt.Core.Models;

public enum FileState { Pending, Ready, DeletePending }

public sealed class FileEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int Number { get; set; }
    public string StorageKey { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public FileState State { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
