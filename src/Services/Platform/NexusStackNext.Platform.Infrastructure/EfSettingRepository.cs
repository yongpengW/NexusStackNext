using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Infrastructure.Persistence;
using Npgsql;

namespace NexusStackNext.Platform.Infrastructure;

internal sealed class EfSettingRepository(PlatformDbContext context) : ISettingRepository
{
    public Task<GlobalSetting?> FindAsync(SettingKey key, CancellationToken cancellationToken = default) =>
        context.Settings.AsNoTracking().SingleOrDefaultAsync(setting => setting.Key == key, cancellationToken);

    public async Task<IReadOnlyList<GlobalSetting>> ListByScopeAsync(string scope, CancellationToken cancellationToken = default) =>
        await context.Settings.AsNoTracking().Where(setting => EF.Property<string>(setting, "Scope") == scope)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Result> AddAsync(GlobalSetting setting, CancellationToken cancellationToken = default)
    {
        context.Settings.Add(setting);
        return await CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<Result> SaveAsync(GlobalSetting setting, long originalVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var entry = context.Attach(setting);
        entry.State = EntityState.Modified;
        entry.Property(item => item.Version).OriginalValue = originalVersion;
        return CommitAsync(cancellationToken);
    }

    private async Task<Result> CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();
            return Result.Failure(SettingStore.Conflict);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_global_settings_key",
        })
        {
            context.ChangeTracker.Clear();
            return Result.Failure(SettingStore.Conflict);
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }
    }
}

/// <summary>Platform 持久存储的显式装配。</summary>
public static class PlatformPersistenceServiceCollectionExtensions
{
    /// <summary>注册 Platform 的 PostgreSQL 适配器。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">Platform 的连接配置。</param>
    /// <returns>原服务集合。</returns>
    public static IServiceCollection AddPlatformPostgresStorage(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<PlatformDbContext>((provider, options) => options
            .UseNexusStackPostgres(connectionString, PlatformDbContext.SchemaName)
            .UseNexusStackInterceptors(provider));
        services.AddScoped<ISettingRepository, EfSettingRepository>();
        services.AddScoped<SettingStore>();
        services.AddHostedService<PlatformDatabaseStartupCheck>();
        services.AddHealthChecks().AddCheck<PlatformDatabaseHealthCheck>("platform-database");
        return services;
    }
}
