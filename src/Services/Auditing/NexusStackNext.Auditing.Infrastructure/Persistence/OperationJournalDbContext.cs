using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using Npgsql;

namespace NexusStackNext.Auditing.Infrastructure.Persistence;

/// <summary>Auditing 在来源宿主拥有的操作日志；发件箱行本身就是持久化观察记录。</summary>
/// <param name="options">本日志独立的连接与事务配置。</param>
public sealed class OperationJournalDbContext(DbContextOptions<OperationJournalDbContext> options)
    : NexusStackDbContext(options, SchemaName)
{
    /// <summary>来源操作日志独占的 schema。</summary>
    public const string SchemaName = "operation_journal";
    internal const string SourceProperty = "Source";
    internal const string OperationIdProperty = "OperationId";
    internal const string PhaseProperty = "Phase";

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        // 来源只追加并投递；中央 Auditing 才消费消息并维护 Inbox。
        modelBuilder.Ignore<InboxMessage>();
        var observation = modelBuilder.Entity<OutboxEntry>();
        observation.Property<string>(SourceProperty).HasMaxLength(64).IsRequired();
        observation.Property<Guid>(OperationIdProperty).IsRequired();
        observation.Property<string>(PhaseProperty).HasMaxLength(16).IsRequired();
        observation.HasIndex(SourceProperty, OperationIdProperty, PhaseProperty).IsUnique();
        observation.HasIndex(item => new { item.DeliveredAt, item.Id }).HasDatabaseName("ix_outbox_delivered_cleanup")
            .HasFilter("\"DeliveredAt\" IS NOT NULL AND \"DeadLetteredAt\" IS NULL");
        modelBuilder.Entity<OperationJournalCapacity>(capacity =>
        {
            capacity.ToTable("capacity", table => table.HasCheckConstraint("ck_capacity_valid", "\"Id\" = 1 AND \"RecordCount\" >= 0 AND \"PayloadBytes\" >= 0"));
            capacity.HasKey(item => item.Id);
            capacity.Property(item => item.Id).ValueGeneratedNever();
        });
        modelBuilder.Entity<OperationJournalRecoveryRecord>(recovery =>
        {
            recovery.ToTable("recovery_records", table => table.HasCheckConstraint("ck_recovery_revision",
                "\"PreviousRetryRevision\" >= 0 AND \"RetryRevision\" = \"PreviousRetryRevision\" + 1"));
            recovery.HasKey(item => item.RequestId);
            recovery.Property(item => item.RequestId).ValueGeneratedNever();
            recovery.Property(item => item.Source).HasMaxLength(64).IsRequired();
            recovery.Property(item => item.Reason).HasMaxLength(32).IsRequired();
            recovery.Property(item => item.Account).HasMaxLength(200).IsRequired();
            recovery.Property(item => item.Machine).HasMaxLength(200).IsRequired();
            recovery.HasIndex(item => new { item.RetainUntil, item.RequestId });
        });
    }
}

internal sealed class OperationJournalCapacity
{
    public int Id { get; set; }
    public long RecordCount { get; set; }
    public long PayloadBytes { get; set; }
}

internal sealed class OperationJournalDbContextFactory : IDesignTimeDbContextFactory<OperationJournalDbContext>
{
    public OperationJournalDbContext CreateDbContext(string[] args) => new(
        OperationJournalDatabase.Options("Host=design-time;Database=design-time"));
}

/// <summary>来源日志的独立迁移入口与数据库选项；普通宿主启动不会迁移。</summary>
public static class OperationJournalDatabase
{
    internal static DbContextOptions<OperationJournalDbContext> Options(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<OperationJournalDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }

    internal static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        string independentConnection;
        try { independentConnection = new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false, IncludeErrorDetail = false }.ConnectionString; }
        catch (ArgumentException) { throw new InvalidOperationException("OperationJournal 连接配置格式无效。"); }
        builder.UseNexusStackPostgres(independentConnection, OperationJournalDbContext.SchemaName);
        // 日志失败的异常只由采集器/投递适配器转换成稳定诊断，不能让 EF 另行回显载荷或服务端错误。
        builder.EnableSensitiveDataLogging(false).EnableDetailedErrors(false).UseLoggerFactory(NullLoggerFactory.Instance);
    }

    /// <summary>显式执行来源操作日志迁移，不启动 HTTP、broker 或业务模块。</summary>
    /// <param name="connectionString">来源操作日志数据库配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>迁移完成。</returns>
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var context = new OperationJournalDbContext(Options(connectionString));
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<OperationJournalCapacity> CheckSchemaAsync(OperationJournalDbContext context, CancellationToken cancellationToken)
    {
        if ((await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            throw new InvalidOperationException("OperationJournal 数据库需要迁移；先执行 migrate-operation-journal。");
        }
        // 查询完整投影，即使没有数据也验证实际列存在，不能仅检查表名。
        _ = await context.Outbox.AsNoTracking().Take(1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return await context.Set<OperationJournalCapacity>().AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken).ConfigureAwait(false);
    }
}
