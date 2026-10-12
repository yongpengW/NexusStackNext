using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Notifications.Domain;

namespace NexusStackNext.Notifications.Infrastructure;

/// <summary>Notifications 独占的发送意图与原 MIME 快照。</summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : NexusStackDbContext(options, SchemaName)
{
    /// <summary>所属数据库 schema。</summary>
    public const string SchemaName = "notifications";
    /// <summary>持久邮件聚合。</summary>
    public DbSet<EmailDelivery> Emails => Set<EmailDelivery>();

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var email = modelBuilder.Entity<EmailDelivery>();
        email.ToTable("emails");
        email.HasKey(item => item.Id);
        email.Property(item => item.Id).HasConversion(id => id.Value, value => new EmailDeliveryId(value));
        email.Property(item => item.OwnerId).HasMaxLength(128).IsRequired();
        email.Property(item => item.Digest).HasMaxLength(64).IsRequired();
        email.Property(item => item.State).HasMaxLength(16).IsRequired();
        email.Property(item => item.ErrorCode).HasMaxLength(64);
        email.Property(item => item.Version).IsConcurrencyToken().ValueGeneratedNever();
        email.Property<byte[]>("MimeContent").IsRequired();
        email.Property<string?>("ExecutionOrigin").HasColumnType("jsonb");
        email.HasIndex(item => new { item.State, item.CreatedAt, item.Id });
    }
}

internal sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<NotificationsDbContext>()
        .UseNexusStackPostgres("Host=design-time;Database=design-time", NotificationsDbContext.SchemaName).Options);
}

/// <summary>独立迁移入口，普通启动不调用它。</summary>
public static class NotificationsDatabase
{
    /// <summary>对指定数据库应用本上下文迁移。</summary>
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var database = new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNexusStackPostgres(connectionString, NotificationsDbContext.SchemaName).Options);
        await database.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
