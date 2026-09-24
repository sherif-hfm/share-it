using Microsoft.EntityFrameworkCore;
using ShareIt.Core.Models;

namespace ShareIt.Infrastructure.Persistence;

public sealed class ShareItDbContext(DbContextOptions<ShareItDbContext> options) : DbContext(options)
{
    public DbSet<SharedSession> Sessions => Set<SharedSession>();
    public DbSet<FileEntry> Files => Set<FileEntry>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        var s = model.Entity<SharedSession>();
        s.HasKey(x => x.Id);
        s.Property(x => x.Id).ValueGeneratedNever();
        model.Entity<TextCard>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<FileEntry>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<BrowserGrant>().Property(x => x.Id).ValueGeneratedNever();
        s.HasIndex(x => x.Code).IsUnique();
        s.Property(x => x.Code).HasMaxLength(6);
        s.HasMany(x => x.Texts).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        s.HasMany(x => x.Files).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        s.HasMany(x => x.Grants).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        s.HasOne(x => x.Cleanup).WithOne().HasForeignKey<CleanupTask>(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<TextCard>().HasIndex(x => new { x.SessionId, x.Number }).IsUnique();
        model.Entity<TextCard>().Property(x => x.Version).IsConcurrencyToken();
        model.Entity<FileEntry>().HasIndex(x => new { x.SessionId, x.Number }).IsUnique();
        model.Entity<FileEntry>().HasIndex(x => x.StorageKey).IsUnique();
        model.Entity<BrowserGrant>().HasIndex(x => new { x.SessionId, x.BrowserId }).IsUnique();
        model.Entity<CleanupTask>().HasKey(x => x.SessionId);
        model.Entity<CleanupTask>().HasIndex(x => x.NextAttemptAtUtc);
        // All timestamps are UTC; restore the Kind SQLite does not retain.
        foreach (var entity in model.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.ClrType == typeof(DateTime))
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
                    v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc)));
        }
    }
}
