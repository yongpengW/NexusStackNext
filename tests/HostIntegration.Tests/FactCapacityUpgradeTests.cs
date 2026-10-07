using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Infrastructure.Persistence;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityUpgradeTests
{
    [PostgresFact]
    public Task PlatformUpgrade_PreservesPolicyAndRetainedUsage() => VerifyUpgradeAsync("platform",
        "20261003102206_SettingFactCapacity", SettingCommittedV1.Name, hasCapacity: true);

    [PostgresFact]
    public Task IdentityUpgrade_PreservesPolicyAndRetainedUsage() => VerifyUpgradeAsync("identity",
        "20261003104239_IdentityFactCapacity", IdentityEntityCommittedV1.Name, hasCapacity: true);

    [PostgresFact]
    public Task FilesUpgrade_AccountsForExistingUtf8Payloads() => VerifyUpgradeAsync("files",
        "20261003094006_CommittedFactCleanup", StoredFileCommittedV1.Name, hasCapacity: false);

    [PostgresFact]
    public Task SchedulingUpgrade_AccountsForExistingUtf8Payloads() => VerifyUpgradeAsync("scheduling",
        "20261003094008_CommittedFactCleanup", PlanCommittedV1.Name, hasCapacity: false);

    private static async Task VerifyUpgradeAsync(string schema, string previousMigration, string eventName, bool hasCapacity)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using var context = CreateContext(schema, database.ConnectionString);
        var migrator = context.GetService<IMigrator>();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(previousMigration));
        if (hasCapacity) { await ConfigureQuotaAsync(context); }
        var at = DateTimeOffset.UtcNow;
        var existing = new OutboxEntry { Id = Guid.NewGuid(), EventName = eventName, Payload = "中", OccurredAt = at };
        var unrelated = existing with { Id = Guid.NewGuid(), EventName = "business.delivery.v1", Payload = new string('x', 100) };
        context.Outbox.AddRange(existing, unrelated);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync());
        // 既有策略不得被迁移或应用重启覆盖；首次创建账本的上下文迁移后才配置测试额度。
        if (!hasCapacity) { await ConfigureQuotaAsync(context); }
        var second = existing with { Id = Guid.NewGuid(), Payload = "文" };
        context.Outbox.Add(second);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var store = new EfOutboxStore<NexusStackDbContext>(context);
        Assert.Equal(new[] { existing.Id, unrelated.Id, second.Id }.Order(),
            (await store.ReadPendingAsync(10, at)).Select(entry => entry.Id).Order());

        // 保持记录额度充足，单独验证升级后的既有 UTF-8 占用不能被遗忘。
        context.Outbox.Add(existing with { Id = Guid.NewGuid(), Payload = "a" });
        var failure = await Record.ExceptionAsync(() => context.SaveChangesAsync());
        if (schema == "identity") { Assert.IsType<IdentityAuditCapacityException>(failure); }
        else
        {
            var persistenceFailure = Assert.IsType<DbUpdateException>(failure);
            Assert.Equal(schema + "_fact_capacity_exhausted", Assert.IsType<PostgresException>(persistenceFailure.InnerException).ConstraintName);
        }
        context.ChangeTracker.Clear();
        Assert.Equal(3, (await store.ReadPendingAsync(10, at)).Count);
    }

    private static Task<int> ConfigureQuotaAsync(NexusStackDbContext context)
    {
        var schema = context.Schema.Replace("\"", "\"\"", StringComparison.Ordinal);
        var sql = $"UPDATE \"{schema}\".fact_capacity SET \"MaxRecords\" = 10, \"MaxPayloadBytes\" = 6, \"MaxRecordPayloadBytes\" = 3";
        return context.Database.ExecuteSqlRawAsync(sql);
    }

    private static NexusStackDbContext CreateContext(string schema, string connectionString) => schema switch
    {
        "platform" => new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres(connectionString, schema).Options),
        "identity" => new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNexusStackPostgres(connectionString, schema).Options),
        "files" => new FilesDbContext(new DbContextOptionsBuilder<FilesDbContext>()
            .UseNexusStackPostgres(connectionString, schema).Options),
        "scheduling" => new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNexusStackPostgres(connectionString, schema).Options),
        _ => throw new ArgumentOutOfRangeException(nameof(schema)),
    };
}
