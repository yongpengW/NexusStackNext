using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Infrastructure.Tasks;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingDbContext(DbContextOptions<PricingDbContext> options) : NexusStackDbContext(options, "pricing")
{
    internal const string CostPayloadHashProperty = "CostPayloadHash";
    public DbSet<PriceQuote> Quotes => Set<PriceQuote>();
    public DbSet<RecalculationEntry> Tasks => Set<RecalculationEntry>();
    public DbSet<DurableTaskAttempt> Attempts => Set<DurableTaskAttempt>();
    public DbSet<PriceCacheInvalidation> CacheInvalidations => Set<PriceCacheInvalidation>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<PriceQuote>().Where(x => x.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            // 所有输入、上游消息和计算结果路径都经过这里，与业务/任务/Inbox 一起提交。
            if (!CacheInvalidations.Local.Any(x => x.ItemId == entry.Entity.Id.Value && x.Version == entry.Entity.Version))
            {
                CacheInvalidations.Add(new PriceCacheInvalidation { ItemId = entry.Entity.Id.Value, Version = entry.Entity.Version });
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("pricing");
        modelBuilder.ConfigureCommittedFactCleanup();
        modelBuilder.ConfigureCommittedFactCapacity();
        modelBuilder.ConfigureFactCapacityPolicy();
        modelBuilder.ConfigurePricingAuditRecovery();
        var invalidation = modelBuilder.Entity<PriceCacheInvalidation>();
        invalidation.ToTable("cache_invalidations");
        invalidation.HasKey(x => new { x.ItemId, x.Version });
        modelBuilder.Entity<InboxMessage>().Property<string?>(CostPayloadHashProperty).HasMaxLength(64);
        var quote = modelBuilder.Entity<PriceQuote>();
        quote.ToTable("quotes");
        quote.HasKey(x => x.Id);
        quote.Property(x => x.Id).HasConversion(x => x.Value, x => new PriceId(x)).ValueGeneratedNever();
        quote.Property(x => x.Cost).HasPrecision(18, 4);
        quote.Property(x => x.FeeRate).HasPrecision(5, 4);
        quote.Property(x => x.BreakEvenPrice).HasPrecision(18, 4);
        quote.Property(x => x.Version).IsConcurrencyToken();
        quote.Ignore(x => x.DomainEvents);

        DurableTaskMapping.Configure<RecalculationEntry>(modelBuilder);
        var task = modelBuilder.Entity<RecalculationEntry>();
        task.Property(x => x.Cost).HasPrecision(18, 4);
        task.Property(x => x.FeeRate).HasPrecision(5, 4);
        task.Property(x => x.Origin).HasMaxLength(24).HasDefaultValue("manual");
        task.HasOne<PriceQuote>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
    }

    public Task<DateTimeOffset> DatabaseTimeAsync(CancellationToken cancellationToken) =>
        Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
}

internal sealed class PriceCacheInvalidation
{
    public Guid ItemId { get; set; }
    public long Version { get; set; }
}

internal sealed class RecalculationEntry : DurableTaskRecord
{
    public PriceId ItemId { get; set; } = null!;
    public long ExpectedVersion { get; set; }
    public decimal Cost { get; set; }
    public decimal FeeRate { get; set; }
    public long InputRevision { get; set; }
    public string Origin { get; set; } = "manual";
    public int DelaySeconds { get; set; }


    public RecalculationStatus ToStatus() => new(TaskId, ItemId.Value, State, InputRevision)
    {
        ExecutionOrigin = ExecutionOrigin,
        Epoch = Epoch,
        Attempts = Attempts,
        ErrorCode = ErrorCode,
        CreatedAt = CreatedAt,
        AvailableAt = AvailableAt,
        LeaseUntil = LeaseUntil,
        MaxLeaseUntil = MaxLeaseUntil,
        History = History.OrderBy(x => x.Epoch).Select(x => new PricingAttempt(x.Epoch, x.StartedAt, x.FinishedAt, x.Outcome, x.ErrorCode)).ToArray(),
    };
    public bool Matches(UpdatePricingCost request) => Origin == "manual" && ItemId.Value == request.ItemId
        && ExpectedVersion == request.ExpectedVersion && Cost == request.Cost && FeeRate == request.FeeRate && DelaySeconds == request.DelaySeconds;
}

/// <summary>迁移工具的显式入口，只从环境读取连接配置。</summary>
internal sealed class PricingDbContextFactory : IDesignTimeDbContextFactory<PricingDbContext>
{
    /// <inheritdoc />
    public PricingDbContext CreateDbContext(string[] args) => PricingDatabase.CreateContext(
        Environment.GetEnvironmentVariable("ConnectionStrings__Pricing")
        ?? throw new InvalidOperationException("必须配置 ConnectionStrings__Pricing。"));
}

/// <summary>独立迁移入口；普通业务请求不建表或迁移。</summary>
public static class PricingDatabase
{
    internal static PricingDbContext CreateContext(string connectionString, IServiceProvider? services = null)
    {
        var builder = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(connectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "pricing"));
        if (services is not null)
        {
            builder.UseNexusStackAuditInterceptor(services)
                .AddInterceptors(services.GetRequiredService<PricingCommittedFactInterceptor>());
        }
        return new PricingDbContext(builder.Options);
    }

    /// <summary>执行 Pricing 的已登记迁移。</summary>
    /// <param name="connectionString">所属数据库。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>迁移完成。</returns>
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>有界检查数据库与迁移状态，不执行迁移。</summary>
    /// <param name="connectionString">所属数据库。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否能处理工作。</returns>
    public static async Task<bool> IsReadyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var context = CreateContext(connectionString);
            if ((await context.Database.GetPendingMigrationsAsync(timeout.Token).ConfigureAwait(false)).Any()) { return false; }
            _ = await context.Quotes.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Tasks.Select(task => new { task.CreatedAt, task.DelaySeconds, task.MaxLeaseUntil }).Take(1).ToArrayAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Attempts.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Inbox.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Outbox.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.CacheInvalidations.AnyAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }
}
