using System.Transactions;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class OperationJournalPersistenceTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task LockedJournalWrite_StopsOnItsCancellationBudget_AndSameObservationCanRecover()
    {
        await using var database = await databases.CreateAsync("journal");
        await using var app = CreateApplication(database.ConnectionString, new() { MaxRecords = 1 });
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var pending = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observation = Started();
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        // 数据库外部边界只阻断写入，SELECT 仍可执行；不依赖产品私有 advisory-lock 键。
        await using (var block = new NpgsqlCommand("LOCK TABLE operation_journal.outbox IN SHARE MODE", blocker, transaction))
        {
            await block.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource();
        // Prime activity statistics before dispatch; the lock proof must observe a later state.
        await using (var initial = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity", blocker, transaction))
        {
            await initial.ExecuteScalarAsync();
        }
        var blockedWrite = journal.AppendAsync(observation, cancellation.Token);
        try
        {
            using var proofTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var waiting = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM pg_locks
                        WHERE database = (SELECT oid FROM pg_database WHERE datname = current_database())
                          AND relation = 'operation_journal.outbox'::regclass AND NOT granted
                          AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))
                    """, blocker, transaction);
                if ((bool)(await waiting.ExecuteScalarAsync(proofTimeout.Token))!) { break; }
                Assert.False(blockedWrite.IsCompleted, "写入必须真正到达被阻断的数据库边界。");
                await Task.Delay(25, proofTimeout.Token);
            }
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedWrite.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(cancellation.IsCancellationRequested);
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
        }

        Assert.Empty(await pending.ReadPendingAsync(10, DateTimeOffset.UtcNow));
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True((await journal.AppendAsync(observation, recoveryTimeout.Token)).IsSuccess);
        Assert.Equal(observation.EventId, Assert.Single(await pending.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Id);
    }

    [PostgresFact]
    public async Task DuplicateAndConflictingStages_PreserveOneImmutableRecordPerPhaseAfterReopen()
    {
        await using var database = await databases.CreateAsync("journal");
        var started = Started();
        var finished = started with
        {
            EventId = Guid.NewGuid(),
            Phase = "finished",
            Outcome = "accepted",
            StatusCode = 202,
            DurationMs = 15,
            ActorId = "operator",
            OccurredAt = started.OccurredAt.AddMilliseconds(15),
        };
        await using (var app = CreateApplication(database.ConnectionString))
        {
            var duplicates = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            {
                await using var scope = app.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(started);
            }));
            Assert.All(duplicates, result => Assert.True(result.IsSuccess));
            await using var write = app.CreateAsyncScope();
            var journal = write.ServiceProvider.GetRequiredService<IOperationJournal>();
            Assert.Equal("operation_journal.identity_conflict", (await journal.AppendAsync(started with { HttpMethod = "PUT" })).Error.Code);
            Assert.Equal("operation_journal.identity_conflict", (await journal.AppendAsync(started with { EventId = Guid.NewGuid() })).Error.Code);
            Assert.True((await journal.AppendAsync(finished)).IsSuccess);
        }

        await using var reopened = CreateApplication(database.ConnectionString);
        await using var read = reopened.CreateAsyncScope();
        var retry = read.ServiceProvider.GetRequiredService<IOperationJournal>();
        Assert.True((await retry.AppendAsync(started)).IsSuccess);
        Assert.True((await retry.AppendAsync(finished)).IsSuccess);
        Assert.Equal("operation_journal.identity_conflict", (await retry.AppendAsync(finished with { ActorId = "changed" })).Error.Code);
        Assert.Equal("operation_journal.identity_conflict", (await retry.AppendAsync(finished with { EventId = Guid.NewGuid() })).Error.Code);
        var pending = read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var records = await pending.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        Assert.Collection(records,
            item => Assert.Equal(started, serializer.Deserialize<OperationObservedV1>(item.Payload)),
            item => Assert.Equal(finished, serializer.Deserialize<OperationObservedV1>(item.Payload)));
    }

    [PostgresFact]
    public async Task CrossedMessageAndPhaseIdentities_AreRejectedWithoutChangingEitherRecordOrFullCapacity()
    {
        await using var database = await databases.CreateAsync("journal");
        var first = Started();
        var second = Started();
        await using (var app = CreateApplication(database.ConnectionString, new() { MaxRecords = 2 }))
        {
            await using var scope = app.CreateAsyncScope();
            var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
            Assert.True((await journal.AppendAsync(first)).IsSuccess);
            Assert.True((await journal.AppendAsync(second)).IsSuccess);
            var crossed = new[] { second with { EventId = first.EventId }, first with { EventId = second.EventId } };
            var rejected = await Task.WhenAll(crossed.Select(async observation =>
            {
                await using var write = app.CreateAsyncScope();
                return await write.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(observation);
            }));
            Assert.All(rejected, result => Assert.Equal("operation_journal.identity_conflict", result.Error.Code));
            Assert.Equal("operation_journal.identity_conflict", (await journal.AppendAsync(first with { EventId = Guid.NewGuid() })).Error.Code);
            Assert.True((await journal.AppendAsync(first)).IsSuccess);
            Assert.True((await journal.AppendAsync(second)).IsSuccess);
        }
        await using var reopened = CreateApplication(database.ConnectionString, new() { MaxRecords = 2 });
        await using var read = reopened.CreateAsyncScope();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, pending.Count);
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var observations = pending.Select(item => serializer.Deserialize<OperationObservedV1>(item.Payload)).ToArray();
        Assert.Contains(first, observations);
        Assert.Contains(second, observations);
        Assert.Equal("operation_journal.capacity_exceeded", (await read.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(Started())).Error.Code);
    }

    [PostgresFact]
    public async Task AmbientBusinessTransactionRollback_DoesNotRollBackTheIndependentJournal()
    {
        await using var database = await databases.CreateAsync("journal");
        var observation = Started();
        await using (var app = CreateApplication(database.ConnectionString))
        await using (var write = app.CreateAsyncScope())
        {
            var journal = write.ServiceProvider.GetRequiredService<IOperationJournal>();
            // 不调用 Complete：退出作用域时外层事务回滚，不能吞掉已独立保存的请求观察。
            using var transaction = new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled);
            Assert.NotNull(Transaction.Current);
            Assert.True((await journal.AppendAsync(observation)).IsSuccess);
        }

        await using var reopened = CreateApplication(database.ConnectionString);
        await using var read = reopened.CreateAsyncScope();
        var pending = read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var saved = Assert.Single(await pending.ReadPendingAsync(10, DateTimeOffset.UtcNow));
        Assert.Equal(observation.EventId, saved.Id);
        Assert.Equal(observation, new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(saved.Payload));
    }

    private static OperationObservedV1 Started() => new()
    {
        EventId = Guid.NewGuid(),
        OperationId = Guid.NewGuid(),
        Source = "pricing",
        Kind = "http",
        Phase = "started",
        TraceId = Guid.NewGuid().ToString("N"),
        HttpMethod = "POST",
        RouteTemplate = "/api/pricing/cost",
        OccurredAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
    };

    private static ServiceProvider CreateApplication(string connectionString, OperationJournalCapacityOptions? capacity = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalPostgresStorage(connectionString, capacity);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
