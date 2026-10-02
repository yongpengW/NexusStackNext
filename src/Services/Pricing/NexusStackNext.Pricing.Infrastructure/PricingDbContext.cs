using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Infrastructure.Tasks;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingDbContext(DbContextOptions<PricingDbContext> options) : NexusStackDbContext(options, "pricing")
{
    public DbSet<PriceQuote> Quotes => Set<PriceQuote>();
    public DbSet<RecalculationEntry> Tasks => Set<RecalculationEntry>();
    public DbSet<DurableTaskAttempt> Attempts => Set<DurableTaskAttempt>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("pricing");
        var quote = modelBuilder.Entity<PriceQuote>();
        quote.ToTable("quotes");
        quote.HasKey(x => x.Id);
        quote.Property(x => x.Id).HasConversion(x => x.Value, x => new PriceId(x)).ValueGeneratedNever();
        quote.Property(x => x.Cost).HasPrecision(18, 4);
        quote.Property(x => x.FeeRate).HasPrecision(5, 4);
        quote.Property(x => x.BreakEvenPrice).HasPrecision(18, 4);
        quote.Property(x => x.Version).IsConcurrencyToken();
        quote.Ignore(x => x.DomainEvents);

        var task = modelBuilder.Entity<RecalculationEntry>();
        task.ToTable("tasks");
        task.HasKey(x => x.TaskId);
        task.Property(x => x.TaskId).ValueGeneratedNever();
        task.Property(x => x.Cost).HasPrecision(18, 4);
        task.Property(x => x.FeeRate).HasPrecision(5, 4);
        task.Property(x => x.State).HasMaxLength(24);
        task.Property(x => x.ErrorCode).HasMaxLength(64);
        task.Property(x => x.Origin).HasMaxLength(24).HasDefaultValue("manual");
        task.Property(x => x.AvailableAt).HasDefaultValueSql("clock_timestamp()");
        task.HasIndex(x => new { x.State, x.AvailableAt });
        task.HasOne<PriceQuote>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        var attempt = modelBuilder.Entity<DurableTaskAttempt>();
        attempt.ToTable("attempts");
        attempt.HasKey(x => new { x.TaskId, x.Epoch });
        attempt.Property(x => x.Outcome).HasMaxLength(24);
        attempt.Property(x => x.ErrorCode).HasMaxLength(64);
        attempt.HasOne<RecalculationEntry>().WithMany(x => x.History).HasForeignKey(x => x.TaskId);
    }

    public Task<DateTimeOffset> DatabaseTimeAsync(CancellationToken cancellationToken) =>
        Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
}

internal sealed class RecalculationEntry : DurableTaskRecord
{
    public PriceId ItemId { get; set; } = null!;
    public long ExpectedVersion { get; set; }
    public decimal Cost { get; set; }
    public decimal FeeRate { get; set; }
    public long InputRevision { get; set; }
    public string Origin { get; set; } = "manual";


    public RecalculationStatus ToStatus() => new(TaskId, ItemId.Value, State, InputRevision)
    {
        Epoch = Epoch,
        Attempts = Attempts,
        ErrorCode = ErrorCode,
        AvailableAt = AvailableAt,
        History = History.OrderBy(x => x.Epoch).Select(x => new PricingAttempt(x.Epoch, x.StartedAt, x.FinishedAt, x.Outcome, x.ErrorCode)).ToArray(),
    };
    public bool Matches(UpdatePricingCost request) => Origin == "manual" && ItemId.Value == request.ItemId
        && ExpectedVersion == request.ExpectedVersion && Cost == request.Cost && FeeRate == request.FeeRate;
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
    internal static PricingDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(connectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "pricing"))
            .Options);

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
            _ = await context.Tasks.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Attempts.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Inbox.AnyAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }
}
