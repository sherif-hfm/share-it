using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ShareIt.Core.Configuration;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;
using ShareIt.Infrastructure.Persistence;
using ShareIt.Infrastructure.Storage;

namespace ShareIt.Infrastructure.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class TestSignals : ISessionChangePublisher, ICleanupSignal
{
    public void Publish(Guid id) { }
    public void Wake() { }
    public Task WaitAsync(TimeSpan interval, CancellationToken ct) => Task.CompletedTask;
}
internal sealed class TestHasher : IPinHasher
{
    public string Hash(string pin) => "hashed:" + pin;
    public bool Verify(string pin, string hash) => hash == Hash(pin);
}

internal sealed class Harness : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "shareit-tests", Guid.NewGuid().ToString("N"));
    public TestClock Clock { get; } = new();
    public ShareItLimits Limits { get; } = new();
    public SessionWorkCoordinator Coordinator { get; } = new();
    public EfShareItPersistence Persistence { get; private set; } = default!;
    public LocalFileStore Store { get; private set; } = default!;
    public IFileStore FileStore { get; set; } = default!;
    public SessionService Sessions { get; private set; } = default!;
    public TextCardService Texts { get; private set; } = default!;
    public FileService Files => new(Persistence, FileStore, Clock, Limits, new TestSignals(), new TestSignals(), Coordinator);
    public SessionPurgeService Purge => new(Persistence, FileStore, Clock, Coordinator, new TestSignals());
    public Caller Caller { get; } = new("first-browser");
    public static async Task<Harness> CreateAsync(string? migration = null)
    {
        var h = new Harness();
        Directory.CreateDirectory(h.Root);
        var factory = new PooledDbContextFactory<ShareItDbContext>(new DbContextOptionsBuilder<ShareItDbContext>().UseSqlite($"Data Source={Path.Combine(h.Root, "test.db")}").Options);
        await using (var db = await factory.CreateDbContextAsync()) await db.GetService<IMigrator>().MigrateAsync(migration);
        h.Persistence = new(factory);
        h.Store = new(Path.Combine(h.Root, "files")); h.FileStore = h.Store;
        h.Sessions = new(h.Persistence, new TestHasher(), h.Clock, h.Limits, new TestSignals(), new TestSignals(), h.Coordinator);
        h.Texts = new(h.Persistence, h.Clock, h.Limits, new TestSignals());
        return h;
    }
    public ValueTask DisposeAsync()
    {
        Persistence.Dispose(); SqliteConnection.ClearAllPools();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shareit-tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(Root, true);
        return ValueTask.CompletedTask;
    }
}
