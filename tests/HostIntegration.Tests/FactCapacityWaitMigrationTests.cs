using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityWaitMigrationTests
{
    [PostgresFact]
    public async Task AllSixPublishedMigrations_UpgradeAndDowngradeWithoutResettingFactsPolicyOrUsage()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await JourneyDatabaseOperation.RunAsync(() => CostingDatabase.MigrateAsync(database.ConnectionString));
        await JourneyDatabaseOperation.RunAsync(() => PricingDatabase.MigrateAsync(database.ConnectionString));
        var sources = new (string Schema, string Event, Migration Migration)[]
        {
            ("platform", "platform.setting-committed.v1", new Platform.Infrastructure.Persistence.Migrations.FactCapacityWaitBudget()),
            ("identity", "identity.entity-committed.v1", new Identity.Infrastructure.Persistence.Migrations.FactCapacityWaitBudget()),
            ("files", "files.stored-file-committed.v1", new Files.Infrastructure.Persistence.Migrations.FactCapacityWaitBudget()),
            ("scheduling", "scheduling.plan-committed.v1", new Scheduling.Infrastructure.Persistence.Migrations.FactCapacityWaitBudget()),
            ("costing", "costing.cost-sheet-committed.v1", new Costing.Infrastructure.Migrations.FactCapacityWaitBudget()),
            ("pricing", "pricing.price-quote-committed.v1", new Pricing.Infrastructure.Migrations.FactCapacityWaitBudget()),
        };
        Assert.Equal(6, sources.Length);
        await using var connection = new NpgsqlConnection(new CommittedFactCapacityWriteOptions { Timeout = TimeSpan.FromMilliseconds(250) }
            .ConfigureConnection(database.ConnectionString));
        await connection.OpenAsync();
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var ddl = new DbContext(new DbContextOptionsBuilder().UseNpgsql(database.ConnectionString).Options);
        foreach (var (schema, factName, migration) in sources)
        {
            // 此测试的公开边界是已发布迁移操作及其 SQL 协议；不访问内部 DbContext 或复制迁移 DDL。
            await ExecuteAsync(connection, $"UPDATE {schema}.fact_capacity SET \"MaxRecords\"=2, \"MaxPayloadBytes\"=6, \"MaxRecordPayloadBytes\"=3");
            await InsertAsync(connection, schema, factName, "中");
            await InsertAsync(connection, schema, "ordinary.business.v1", "not charged to fact quota");
            await ApplyAsync(ddl, migration, down: true);
            Assert.Equal(new long[] { 2, 6, 3, 1, 3 }, await ReadLedgerAsync(connection, schema));
            await ExecuteAsync(connection, "SET lock_timeout = '100ms'");
            await using (var transaction = await blocker.BeginTransactionAsync())
            {
                await using (var hold = new NpgsqlCommand($"SELECT 1 FROM {schema}.fact_capacity WHERE \"Id\"=1 FOR UPDATE", blocker, transaction))
                { Assert.Equal(1, await hold.ExecuteScalarAsync()); }
                var oldProtocol = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(connection, schema, factName, "文"));
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, oldProtocol.SqlState);
                Assert.True(oldProtocol.IsTransient);
                await transaction.RollbackAsync();
            }
            await ApplyAsync(ddl, migration, down: false);
            Assert.Equal(new long[] { 2, 6, 3, 1, 3 }, await ReadLedgerAsync(connection, schema));
            await using (var transaction = await blocker.BeginTransactionAsync())
            {
                await using (var hold = new NpgsqlCommand($"SELECT 1 FROM {schema}.fact_capacity WHERE \"Id\"=1 FOR UPDATE", blocker, transaction))
                { Assert.Equal(1, await hold.ExecuteScalarAsync()); }
                var current = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(connection, schema, factName, "文"));
                Assert.Equal("P0001", current.SqlState);
                Assert.Equal(schema + "_fact_capacity_busy", current.ConstraintName);
                Assert.False(current.IsTransient);
                await transaction.RollbackAsync();
            }
            await InsertAsync(connection, schema, factName, "文");
            var exhausted = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(connection, schema, factName, "a"));
            Assert.Equal(schema + "_fact_capacity_exhausted", exhausted.ConstraintName);
            var immutable = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
                $"UPDATE {schema}.outbox SET \"Payload\"='a' WHERE \"EventName\"='{factName}'"));
            Assert.Equal(schema + "_fact_immutable", immutable.ConstraintName);
            Assert.Equal(new long[] { 2, 6, 3, 2, 6 }, await ReadLedgerAsync(connection, schema));
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {schema}.outbox", connection);
            Assert.Equal(3L, await count.ExecuteScalarAsync());
        }
    }

    private static async Task ApplyAsync(DbContext context, Migration migration, bool down)
    {
        await using var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true);
        migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
        var operations = down ? migration.DownOperations : migration.UpOperations;
        Assert.NotEmpty(operations);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, migration.TargetModel);
        Assert.NotEmpty(commands);
        await using var transaction = await context.Database.BeginTransactionAsync();
        foreach (var command in commands)
        {
            Assert.False(command.TransactionSuppressed);
            await context.Database.ExecuteSqlRawAsync(command.CommandText);
        }
        await transaction.CommitAsync();
    }

    private static async Task InsertAsync(NpgsqlConnection connection, string schema, string eventName, string payload)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {schema}.outbox (\"Id\", \"EventName\", \"Payload\", \"OccurredAt\", \"AttemptCount\") VALUES (@id, @event, @payload, @at, 0)", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("event", eventName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long[]> ReadLedgerAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand($"SELECT \"MaxRecords\", \"MaxPayloadBytes\", \"MaxRecordPayloadBytes\", \"RetainedRecords\", \"RetainedPayloadBytes\" FROM {schema}.fact_capacity WHERE \"Id\"=1", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return [reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)];
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
