using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityWaitProtocolTests
{
    [PostgresFact]
    public async Task CapacityLocks_PreserveShorterCallerBudgetAndSessionOptions_WithoutBlockingOrdinaryMessages()
    {
        foreach (var (original, milliseconds, tableLock) in new[] { ("100ms", 5000, false), ("10s", 250, true) })
        {
            await using var database = await IdentityJourneyDatabase.CreateAsync();
            await using var context = CreateContext(database.ConnectionString, milliseconds);
            await context.Database.MigrateAsync();
            await context.Database.OpenConnectionAsync();
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            await using (var configure = new NpgsqlCommand("SELECT set_config('lock_timeout', @timeout, false), set_config('statement_timeout', '10s', false)", connection))
            {
                configure.Parameters.AddWithValue("timeout", original);
                await configure.ExecuteNonQueryAsync();
            }
            await using var blocker = new NpgsqlConnection(database.ConnectionString);
            await blocker.OpenAsync();
            await using var blocking = await blocker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(tableLock
                ? "LOCK TABLE platform.fact_capacity IN ACCESS EXCLUSIVE MODE"
                : "SELECT 1 FROM platform.fact_capacity WHERE \"Id\" = 1 FOR UPDATE", blocker, blocking))
            {
                if (tableLock) { await command.ExecuteNonQueryAsync(); }
                else { Assert.Equal(1, await command.ExecuteScalarAsync()); }
            }
            await using var transaction = await context.Database.BeginTransactionAsync();
            var now = DateTimeOffset.UtcNow;
            var ordinary = new OutboxEntry { Id = Guid.NewGuid(), EventName = "ordinary.business.v1", Payload = "{}", OccurredAt = now };
            var first = ordinary with { Id = Guid.NewGuid(), EventName = SettingCommittedV1.Name, Payload = "中" };
            var second = first with { Id = Guid.NewGuid(), Payload = "文" };
            context.Outbox.AddRange(ordinary, first, second);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(deadline.Token));
            var rejection = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal("P0001", rejection.SqlState);
            Assert.Equal("platform_fact_capacity_busy", rejection.ConstraintName);
            Assert.False(rejection.IsTransient);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "不得扩大较短的调用者预算，也不得等待普通命令超时。");
            context.ChangeTracker.Clear();
            var store = new EfOutboxStore<PlatformDbContext>(context);
            Assert.Empty(await store.ReadPendingAsync(10, now));
            await AssertSessionOptionsAsync(connection, original);

            // EF 的失败保存点已回滚整批，外层事务仍可继续；普通消息不获取容量锁。
            context.Outbox.Add(ordinary);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Assert.Equal(ordinary.Id, Assert.Single(await store.ReadPendingAsync(10, now)).Id);
            await AssertSessionOptionsAsync(connection, original);
            await blocking.RollbackAsync();
            context.Outbox.AddRange(first, second);
            await context.SaveChangesAsync();
            await AssertSessionOptionsAsync(connection, original);
            await transaction.CommitAsync();
            var snapshot = (await new PostgresCommittedFactCapacityReader<PlatformDbContext>(context, "platform", new()).ReadAsync()).Value;
            Assert.Equal(2, snapshot.RetainedRecords);
            Assert.Equal(6, snapshot.RetainedPayloadBytes);
            Assert.Equal(100000, snapshot.MaxRecords);
        }
    }

    [PostgresFact]
    public async Task CleanupLedgerContention_PreservesWholeConfirmedBatch_AndRecoversWithoutLeakingQuota()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString, 250);
        await context.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var first = new OutboxEntry { Id = Guid.NewGuid(), EventName = SettingCommittedV1.Name, Payload = "中", OccurredAt = now.AddDays(-8), DeliveredAt = now.AddDays(-8) };
        var second = first with { Id = Guid.NewGuid() };
        var pending = first with { Id = Guid.NewGuid(), DeliveredAt = null };
        var ordinary = first with { Id = Guid.NewGuid(), EventName = "ordinary.business.v1" };
        context.Outbox.AddRange(first, second, pending, ordinary);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var reader = new PostgresCommittedFactCapacityReader<PlatformDbContext>(context, "platform", new());
        var before = (await reader.ReadAsync()).Value;
        Assert.Equal(3, before.RetainedRecords);
        Assert.Equal(9, before.RetainedPayloadBytes);
        var cleanup = new EfCommittedFactCleanup<PlatformDbContext>(context, SettingCommittedV1.Name, new(), new FixedClock(now));
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await using (var command = new NpgsqlCommand("SELECT 1 FROM platform.fact_capacity WHERE \"Id\" = 1 FOR UPDATE", blocker, transaction))
            { Assert.Equal(1, await command.ExecuteScalarAsync()); }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var failure = await Assert.ThrowsAsync<PostgresException>(() => cleanup.CleanupAsync(deadline.Token));
            Assert.Equal("platform_fact_capacity_busy", failure.ConstraintName);
            Assert.False(failure.IsTransient);
            Assert.Equal(before, (await reader.ReadAsync()).Value);
            await transaction.RollbackAsync();
        }
        Assert.Equal(2, await cleanup.CleanupAsync());
        Assert.Equal(0, await cleanup.CleanupAsync());
        var after = (await reader.ReadAsync()).Value;
        Assert.Equal(1, after.RetainedRecords);
        Assert.Equal(3, after.RetainedPayloadBytes);
        Assert.Equal(before.MaxRecords, after.MaxRecords);
        var remaining = await context.Outbox.AsNoTracking().Select(entry => entry.Id).ToArrayAsync();
        Assert.Equal(new[] { pending.Id, ordinary.Id }.Order(), remaining.Order());
    }

    private static PlatformDbContext CreateContext(string connectionString, int milliseconds)
        => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(
            new CommittedFactCapacityWriteOptions { Timeout = TimeSpan.FromMilliseconds(milliseconds) }.ConfigureConnection(connectionString),
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", "platform")).Options);

    private static async Task AssertSessionOptionsAsync(NpgsqlConnection connection, string original)
    {
        await using var command = new NpgsqlCommand("SELECT current_setting('lock_timeout'), current_setting('statement_timeout')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(original, reader.GetString(0));
        Assert.Equal("10s", reader.GetString(1));
    }
}
