using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Infrastructure.Tasks;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Domain;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class CostingDbContext(DbContextOptions<CostingDbContext> options) : NexusStackDbContext(options, "costing")
{
    public DbSet<CostSheet> Sheets => Set<CostSheet>();
    public DbSet<CostCalculationEntry> Tasks => Set<CostCalculationEntry>();
    public DbSet<DurableTaskAttempt> Attempts => Set<DurableTaskAttempt>();
    public DbSet<ScheduledCostReceiptEntry> ScheduleReceipts => Set<ScheduledCostReceiptEntry>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("costing");
        var sheet = modelBuilder.Entity<CostSheet>();
        sheet.ToTable("sheets");
        sheet.HasKey(x => x.Id);
        sheet.Property(x => x.Id).HasConversion(x => x.Value, x => new CostId(x)).ValueGeneratedNever();
        sheet.Property(x => x.PurchaseCost).HasPrecision(18, 4);
        sheet.Property(x => x.FreightCost).HasPrecision(18, 4);
        sheet.Property(x => x.UnitCost).HasPrecision(18, 4);
        sheet.Property(x => x.Version).IsConcurrencyToken();
        sheet.Ignore(x => x.DomainEvents);

        DurableTaskMapping.Configure<CostCalculationEntry>(modelBuilder);
        var task = modelBuilder.Entity<CostCalculationEntry>();
        task.Property(x => x.PurchaseCost).HasPrecision(18, 4);
        task.Property(x => x.FreightCost).HasPrecision(18, 4);
        task.HasOne<CostSheet>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        task.Property(x => x.Origin).HasMaxLength(32).HasDefaultValue("manual");
        var receipt = modelBuilder.Entity<ScheduledCostReceiptEntry>();
        receipt.ToTable("schedule_receipts");
        receipt.HasKey(x => x.OccurrenceId);
        receipt.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        receipt.Property(x => x.CreatedBy).HasMaxLength(128).IsRequired();
        receipt.Property(x => x.Decision).HasMaxLength(16).IsRequired();
        receipt.Property(x => x.ErrorCode).HasMaxLength(128);
        receipt.HasOne<CostCalculationEntry>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
    }

    public Task<DateTimeOffset> DatabaseTimeAsync(CancellationToken cancellationToken) =>
        Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
}

internal sealed class CostCalculationEntry : DurableTaskRecord
{
    public string Origin { get; set; } = "manual";
    public CostId ItemId { get; set; } = null!;
    public long ExpectedVersion { get; set; }
    public decimal PurchaseCost { get; set; }
    public decimal FreightCost { get; set; }
    public long InputRevision { get; set; }


    public CostCalculationStatus ToStatus() => new(TaskId, ItemId.Value, State, InputRevision)
    {
        ExecutionOrigin = ExecutionOrigin,
        Epoch = Epoch,
        Attempts = Attempts,
        ErrorCode = ErrorCode,
        AvailableAt = AvailableAt,
        History = History.OrderBy(x => x.Epoch).Select(x => new CostingAttempt(x.Epoch, x.StartedAt, x.FinishedAt, x.Outcome, x.ErrorCode)).ToArray(),
    };
    public bool Matches(UpdateCostInputs request) => Origin == "manual" && ItemId.Value == request.ItemId
        && ExpectedVersion == request.ExpectedVersion && PurchaseCost == request.PurchaseCost && FreightCost == request.FreightCost;
}

internal sealed class ScheduledCostReceiptEntry
{
    public Guid OccurrenceId { get; set; }
    public long PlanId { get; set; }
    public long TriggerSequence { get; set; }
    public Guid ItemId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public Guid? TaskId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string PayloadHash { get; set; } = string.Empty;

    public ScheduledCostReceipt ToView() => new(OccurrenceId, PlanId, TriggerSequence, ItemId, CreatedBy, Decision, TaskId, ErrorCode, ReceivedAt);
}

/// <summary>迁移工具的显式入口，只从环境读取连接配置。</summary>
internal sealed class CostingDbContextFactory : IDesignTimeDbContextFactory<CostingDbContext>
{
    /// <inheritdoc />
    public CostingDbContext CreateDbContext(string[] args) => CostingDatabase.CreateContext(
        Environment.GetEnvironmentVariable("ConnectionStrings__Costing")
        ?? throw new InvalidOperationException("必须配置 ConnectionStrings__Costing。"));
}

/// <summary>独立迁移入口；普通业务请求不建表或迁移。</summary>
public static class CostingDatabase
{
    internal static CostingDbContext CreateContext(string connectionString, IServiceProvider? services = null)
    {
        var builder = new DbContextOptionsBuilder<CostingDbContext>()
            .UseNpgsql(connectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "costing"));
        if (services is not null) { builder.UseNexusStackAuditInterceptor(services); }
        return new CostingDbContext(builder.Options);
    }

    /// <summary>执行 Costing 的已登记迁移。</summary>
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
            _ = await context.Sheets.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Tasks.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Attempts.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.Outbox.AnyAsync(timeout.Token).ConfigureAwait(false);
            _ = await context.ScheduleReceipts.AnyAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }
}
