using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationJournalDeliveryTests
{
    [PostgresFact]
    public async Task PostgresRecovery_RejectsOldPublisherFailures_ButAcceptsItsRealConfirmation()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        await using var app = PostgresApplication(database.ConnectionString);
        await AssertInFlightRecoveryAsync(app, 1, false);
        await AssertInFlightRecoveryAsync(app, 2, false);
        await AssertInFlightRecoveryAsync(app, 1, true);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task MemoryRecovery_RejectsOldPublisherFailures_ButAcceptsItsRealConfirmation(int maxAttempts, bool succeeds)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalMemoryStorage();
        await using var app = services.BuildServiceProvider();
        await AssertInFlightRecoveryAsync(app, maxAttempts, succeeds);
    }

    private static async Task AssertInFlightRecoveryAsync(ServiceProvider app, int maxAttempts, bool succeeds)
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var message = Started(now);
        Assert.True((await journal.AppendAsync(message)).IsSuccess);
        var bus = new PausedBus();
        var publisher = new OutboxPublisher(outbox, bus, new NexusStackNext.TestSupport.FixedClock(now), new() { MaxAttempts = maxAttempts });
        var inFlight = publisher.PublishPendingAsync();
        await bus.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await using var other = app.CreateAsyncScope();
            var otherOutbox = other.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            await otherOutbox.MarkDeadLetteredAsync(message.EventId, "other publisher stopped", now, 0);
            Assert.True((await RetryAsync(maintenance, message.EventId, now, 0)).IsSuccess);
        }
        finally { bus.Complete.TrySetResult(succeeds ? Result.Success() : Result.Failure(new("broker.failed", "private failure"))); }
        var result = await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(succeeds ? 1 : 0, result.Delivered);
        Assert.Equal(0, result.Retried);
        Assert.Equal(0, result.DeadLettered);
        var current = (await maintenance.GetDeliveryAsync(message.EventId)).Value;
        Assert.Equal(succeeds ? "Delivered" : "Pending", current.State);
        Assert.Equal(1, current.RetryRevision);
        Assert.Equal(0, current.AttemptCount);
        Assert.Null(current.FailureCode);
        Assert.Null(current.NextAttemptAt);
        if (!succeeds)
        {
            var fresh = new OutboxPublisher(outbox, new PausedBus(Result.Failure(new("broker.failed", "still unavailable"))),
                new NexusStackNext.TestSupport.FixedClock(now), new() { MaxAttempts = 1 });
            await fresh.PublishPendingAsync();
            Assert.Equal("DeadLettered", (await maintenance.GetDeliveryAsync(message.EventId)).Value.State);
        }
    }

    private sealed class PausedBus : IEventBus
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Result> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PausedBus(Result? result = null)
        {
            if (result is not null) { Complete.SetResult(result); }
        }
        public async Task<Result> PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            return await Complete.Task.WaitAsync(cancellationToken);
        }
    }

    [PostgresFact]
    public async Task RecoveryCancellationAndSaveFailure_PreserveStoppedEvidence_AndAllowLaterRecovery()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        await using var app = PostgresApplication(database.ConnectionString);
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var message = Started(now);
        Assert.True((await journal.AppendAsync(message)).IsSuccess);
        await outbox.MarkDeadLetteredAsync(message.EventId, "stopped", now, 0);
        var stopped = (await maintenance.GetDeliveryAsync(message.EventId)).Value;
        var request = new OperationJournalRecoveryRequest(Guid.NewGuid(), message.EventId, now, 0, "dependency-restored");
        var actor = new OperationJournalRecoveryActor("test-operator", "test-host");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var block = new NpgsqlCommand("LOCK TABLE operation_journal.outbox IN ACCESS EXCLUSIVE MODE", connection, transaction))
            { await block.ExecuteNonQueryAsync(); }
            using var cancellation = new CancellationTokenSource();
            var attempt = maintenance.RetryDeliveryAsync(request, actor, cancellation.Token);
            try
            {
                using var proof = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    await using var waiting = new NpgsqlCommand("""
                        SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                            AND wait_event_type = 'Lock' AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))
                        """, connection, transaction);
                    if ((bool)(await waiting.ExecuteScalarAsync(proof.Token))!) { break; }
                    Assert.False(attempt.IsCompleted);
                    await Task.Delay(25, proof.Token);
                }
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { cancellation.Cancel(); await transaction.RollbackAsync(); }
        }
        Assert.Equal(stopped, (await maintenance.GetDeliveryAsync(message.EventId)).Value);
        Assert.Equal(OperationJournalRecoveryErrors.NotFound, (await maintenance.GetRecoveryAsync(request.RequestId)).Error);
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION operation_journal.reject_retry() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected private recovery failure'; END $$;
            CREATE TRIGGER reject_retry BEFORE UPDATE ON operation_journal.outbox
                FOR EACH ROW EXECUTE FUNCTION operation_journal.reject_retry();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<DbUpdateException>(() => maintenance.RetryDeliveryAsync(request, actor));
        Assert.Equal(stopped, (await maintenance.GetDeliveryAsync(message.EventId)).Value);
        Assert.Equal(OperationJournalRecoveryErrors.NotFound, (await maintenance.GetRecoveryAsync(request.RequestId)).Error);
        await using (var release = new NpgsqlCommand("DROP TRIGGER reject_retry ON operation_journal.outbox", connection))
        { await release.ExecuteNonQueryAsync(); }
        await using (var inject = new NpgsqlCommand("""
            CREATE TRIGGER reject_receipt BEFORE INSERT ON operation_journal.recovery_records
                FOR EACH ROW EXECUTE FUNCTION operation_journal.reject_retry();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<DbUpdateException>(() => maintenance.RetryDeliveryAsync(request, actor));
        Assert.Equal(stopped, (await maintenance.GetDeliveryAsync(message.EventId)).Value);
        Assert.Equal(OperationJournalRecoveryErrors.NotFound, (await maintenance.GetRecoveryAsync(request.RequestId)).Error);
        await using (var release = new NpgsqlCommand("DROP TRIGGER reject_receipt ON operation_journal.recovery_records", connection))
        { await release.ExecuteNonQueryAsync(); }
        var recovered = (await maintenance.RetryDeliveryAsync(request, actor)).Value;
        Assert.Equal(1, recovered.RetryRevision);
        Assert.Equal(recovered, (await maintenance.GetRecoveryAsync(request.RequestId)).Value);
    }

    [PostgresFact]
    public async Task ConcurrentRecovery_HasOneWinner_AndRevisionSurvivesReopening()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var message = Started(now);
        await using (var app = PostgresApplication(database.ConnectionString))
        {
            await using var seed = app.CreateAsyncScope();
            Assert.True((await seed.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(message)).IsSuccess);
            var outbox = seed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            await outbox.MarkDeadLetteredAsync(message.EventId, "stopped", now, 0);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retries = Enumerable.Range(0, 6).Select(async _ =>
            {
                await using var scope = app.CreateAsyncScope();
                await release.Task;
                return await RetryAsync(scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>(), message.EventId, now, 0);
            }).ToArray();
            release.SetResult();
            var results = await Task.WhenAll(retries);
            Assert.Single(results, result => result.IsSuccess);
            Assert.Equal(5, results.Count(result => result.IsFailure && result.Error == OperationJournalDeliveryErrors.Conflict));
            // 旧作用域确认时，不能把另一个上下文保存的恢复版本覆盖掉。
            await outbox.MarkDeliveredAsync(message.EventId, now.AddMinutes(1));
            await outbox.MarkFailedAsync(message.EventId, "late", now.AddMinutes(2), 0);
            await outbox.MarkDeadLetteredAsync(message.EventId, "late", now.AddMinutes(2), 0);
        }
        await using var reopened = PostgresApplication(database.ConnectionString);
        await using var read = reopened.CreateAsyncScope();
        var maintenance = read.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var delivered = (await maintenance.GetDeliveryAsync(message.EventId)).Value;
        Assert.Equal("Delivered", delivered.State);
        Assert.Equal(1, delivered.RetryRevision);
        Assert.Null(delivered.FailureCode);
        Assert.True((await RetryAsync(maintenance, message.EventId, now, 0)).IsFailure);
        Assert.Equal(1, await maintenance.CleanupDeliveredAsync());
        Assert.Equal(OperationJournalDeliveryErrors.NotFound, (await maintenance.GetDeliveryAsync(message.EventId)).Error);
        Assert.Equal(OperationJournalDeliveryErrors.NotFound, (await RetryAsync(maintenance, message.EventId, now, 1)).Error);
    }

    private static ServiceProvider PostgresApplication(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NexusStackNext.BuildingBlocks.Application.Time.IClock>(
            new NexusStackNext.TestSupport.FixedClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)));
        services.AddOperationJournalPostgresStorage(connectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task MemoryDeadLetters_AreBoundedFilteredSafeAndRemovedFromTheListAfterRetry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalMemoryStorage();
        await using var app = services.BuildServiceProvider();
        await AssertDeadLetterQueryAsync(app);
    }

    [PostgresFact]
    public async Task PostgresDeadLetters_AreBoundedFilteredSafeAndRemovedFromTheListAfterRetry()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        await using var app = services.BuildServiceProvider();
        await AssertDeadLetterQueryAsync(app);
    }

    private static async Task AssertDeadLetterQueryAsync(ServiceProvider app)
    {
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var management = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var messages = Enumerable.Range(0, 5).Select(index => Started(now) with { Source = index == 2 ? "costing" : "pricing" }).ToArray();
        for (var index = 0; index < messages.Length; index++)
        {
            Assert.True((await journal.AppendAsync(messages[index])).IsSuccess);
            if (index < 4) { await outbox.MarkDeadLetteredAsync(messages[index].EventId, "private failure", now.AddSeconds(index), 0); }
        }
        await outbox.MarkDeliveredAsync(messages[3].EventId, now.AddMinutes(1));
        var first = (await management.QueryDeadLettersAsync(new(Limit: 1, Source: "pricing"))).Value;
        Assert.Equal(2, first.Total);
        Assert.Equal(messages[0].EventId, Assert.Single(first.Items).MessageId);
        var second = (await management.QueryDeadLettersAsync(new(Page: 2, Limit: 1, Source: "pricing"))).Value;
        Assert.Equal(messages[1].EventId, Assert.Single(second.Items).MessageId);
        Assert.Empty((await management.QueryDeadLettersAsync(new(Page: 3, Limit: 1, Source: "pricing"))).Value.Items);
        var all = (await management.QueryDeadLettersAsync(new())).Value;
        Assert.Equal(3, all.Total);
        Assert.All(all.Items, item => Assert.Equal("DeadLettered", item.State));
        var wire = System.Text.Json.JsonSerializer.Serialize(all);
        Assert.DoesNotContain("private", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("delivery-test", wire, StringComparison.Ordinal);
        Assert.Empty((await management.QueryDeadLettersAsync(new(Source: "missing"))).Value.Items);
        foreach (var query in new OperationJournalDeadLetterQuery[] { new(Page: 0), new(Page: 1001), new(Limit: 0), new(Limit: 101), new(Source: ""), new(Source: "bad\nsource"), new(Source: new string('x', 65)) })
        {
            Assert.Equal(OperationJournalDeliveryErrors.InvalidQuery, (await management.QueryDeadLettersAsync(query)).Error);
        }
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => management.GetDeliveryAsync(messages[0].EventId, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => management.QueryDeadLettersAsync(new(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RetryAsync(management, messages[0].EventId, now, 0, canceled.Token));
        Assert.True((await RetryAsync(management, messages[0].EventId, now, 0)).IsSuccess);
        Assert.Equal(1, (await management.QueryDeadLettersAsync(new(Source: "pricing"))).Value.Total);
        Assert.Equal(OperationJournalDeliveryErrors.NotFound, (await management.GetDeliveryAsync(Guid.NewGuid())).Error);
        Assert.Equal(OperationJournalDeliveryErrors.NotFound, (await RetryAsync(management, Guid.NewGuid(), now, 0)).Error);
    }

    [PostgresFact]
    public async Task PostgresRetry_PreservesTheOriginalMessage_AndRejectsStaleAndRepeatedRecovery()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 1 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await AssertRetryAsync(app);
    }

    [Fact]
    public async Task MemoryRetry_PreservesTheOriginalMessage_AndRejectsStaleAndRepeatedRecovery()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalMemoryStorage(new() { MaxRecords = 1 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await AssertRetryAsync(app);
    }

    private static async Task AssertRetryAsync(ServiceProvider app)
    {
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var management = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var stoppedAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var message = Started(stoppedAt);
        Assert.True((await journal.AppendAsync(message)).IsSuccess);
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var pending = (await management.GetDeliveryAsync(message.EventId)).Value;
        Assert.Equal("Pending", pending.State);
        Assert.Equal(0, pending.RetryRevision);
        Assert.True((await RetryAsync(management, message.EventId, stoppedAt, 0)).IsFailure);
        await outbox.MarkDeadLetteredAsync(message.EventId, "private broker exception", stoppedAt, 0);
        var stopped = (await management.GetDeliveryAsync(message.EventId)).Value;
        Assert.Equal("DeadLettered", stopped.State);
        Assert.Equal("operation_journal.delivery_failed", stopped.FailureCode);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(stopped), StringComparison.Ordinal);
        Assert.True((await RetryAsync(management, message.EventId, stoppedAt.AddSeconds(1), 0)).IsFailure);
        Assert.True((await RetryAsync(management, message.EventId, stoppedAt, 1)).IsFailure);
        Assert.True((await RetryAsync(management, message.EventId, stoppedAt, 0)).IsSuccess);
        var retry = (await management.GetDeliveryAsync(message.EventId)).Value;
        Assert.Equal("Pending", retry.State);
        Assert.Equal(1, retry.RetryRevision);
        Assert.Equal(0, retry.AttemptCount);
        Assert.Null(retry.FailureCode);
        Assert.Equal(original.ToEnvelope(), Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).ToEnvelope());
        Assert.True((await RetryAsync(management, message.EventId, stoppedAt, 0)).IsFailure);
        // 相同时间再次停止，旧恢复请求也不得误命中新一轮。
        await outbox.MarkDeadLetteredAsync(message.EventId, "private second failure", stoppedAt, 1);
        Assert.True((await RetryAsync(management, message.EventId, stoppedAt, 0)).IsFailure);
        Assert.True((await RetryAsync(management, message.EventId, stoppedAt, 1)).IsSuccess);
        await outbox.MarkDeliveredAsync(message.EventId, stoppedAt.AddMinutes(1));
        Assert.True((await RetryAsync(management, message.EventId, stoppedAt, 2)).IsFailure);
        Assert.Equal("Delivered", (await management.GetDeliveryAsync(message.EventId)).Value.State);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started(stoppedAt))).Error.Code);
    }

    private static Task<Result<OperationJournalRecoveryReceipt>> RetryAsync(IOperationJournalMaintenance maintenance,
        Guid messageId, DateTimeOffset stoppedAt, long revision, CancellationToken cancellationToken = default) =>
        maintenance.RetryDeliveryAsync(new(Guid.NewGuid(), messageId, stoppedAt, revision, "dependency-restored"),
            new("test-operator", "test-host"), cancellationToken);
    private static OperationObservedV1 Started(DateTimeOffset now) => new()
    {
        EventId = Guid.NewGuid(),
        OperationId = Guid.NewGuid(),
        Source = "pricing",
        Kind = "http",
        Phase = "started",
        TraceId = "delivery-test",
        HttpMethod = "POST",
        RouteTemplate = "/api/pricing/cost",
        OccurredAt = now,
    };
}
