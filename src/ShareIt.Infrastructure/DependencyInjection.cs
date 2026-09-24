using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShareIt.Core.Contracts;
using ShareIt.Core.Services;
using ShareIt.Infrastructure.BackgroundJobs;
using ShareIt.Infrastructure.Persistence;
using ShareIt.Infrastructure.Security;
using ShareIt.Infrastructure.Storage;

namespace ShareIt.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddShareItInfrastructure(this IServiceCollection services, string dataPath, string? pepperFile)
    {
        Directory.CreateDirectory(dataPath);
        services.AddDbContextFactory<ShareItDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataPath, "shareit.db")};Default Timeout=15"));
        services.AddSingleton<IShareItPersistence, EfShareItPersistence>();
        services.AddSingleton<IFileStore>(new LocalFileStore(Path.Combine(dataPath, "files")));
        services.AddSingleton<IPinHasher>(new PinHasher(pepperFile ?? Path.Combine(dataPath, "secrets", "pin-pepper")));
        services.AddSingleton<ICleanupSignal, CleanupSignal>();
        services.AddSingleton<SessionWorkCoordinator>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<TextCardService>();
        services.AddSingleton<FileService>();
        services.AddSingleton<SessionPurgeService>();
        services.AddHostedService<CleanupWorker>();
        return services;
    }
}
