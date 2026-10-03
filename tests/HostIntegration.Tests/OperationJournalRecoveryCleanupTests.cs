using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationJournalRecoveryCleanupTests
{
    [PostgresFact]
    public async Task PostgresCleanup_RetainsOriginalDeadlineAfterShorteningPolicyAndRestarting()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var message = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "pricing",
            Kind = "http",
            Phase = "started",
            TraceId = "recovery-policy-change",
            HttpMethod = "POST",
            OccurredAt = now,
        };
        var request = new OperationJournalRecoveryRequest(Guid.NewGuid(), message.EventId, now, 0, "manual-retry");
        var actor = new OperationJournalRecoveryActor("test-operator", "test-host");
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock);
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        OperationJournalRecoveryReceipt original;
        await using (var initial = services.BuildServiceProvider())
        {
            await using var seed = initial.CreateAsyncScope();
            Assert.True((await seed.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(message)).IsSuccess);
            Assert.True(await seed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
                .MarkDeadLetteredAsync(message.EventId, "stopped", now, 0));
            original = (await seed.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>().RetryDeliveryAsync(request, actor)).Value;
        }
        Assert.Equal(now.AddDays(30), original.RetainUntil);
        clock.Advance(TimeSpan.FromHours(2));
        var reopenedServices = new ServiceCollection();
        reopenedServices.AddSingleton<IClock>(clock);
        reopenedServices.AddOperationJournalPostgresStorage(database.ConnectionString, cleanup: new() { RecoveryRetention = TimeSpan.FromHours(1) });
        await using var reopened = reopenedServices.BuildServiceProvider();
        await using var scope = reopened.CreateAsyncScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();

        Assert.Equal(0, await maintenance.CleanupRecoveryRecordsAsync());
        Assert.Equal(original, (await maintenance.RetryDeliveryAsync(request, actor)).Value);
        Assert.True(await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
            .MarkDeadLetteredAsync(message.EventId, "stopped again", clock.UtcNow, 1));
        var nextRequest = request with { RequestId = Guid.NewGuid(), ExpectedDeadLetteredAt = clock.UtcNow, ExpectedRetryRevision = 1 };
        var newer = (await maintenance.RetryDeliveryAsync(nextRequest, actor)).Value;
        Assert.Equal(now.AddHours(3), newer.RetainUntil);
        clock.UtcNow = newer.RetainUntil;
        Assert.Equal(1, await maintenance.CleanupRecoveryRecordsAsync());
        Assert.Equal(OperationJournalRecoveryErrors.NotFound, (await maintenance.GetRecoveryAsync(nextRequest.RequestId)).Error);
        Assert.Equal(original, (await maintenance.GetRecoveryAsync(request.RequestId)).Value);
    }

    [Fact]
    public Task HostMemoryCleanup_ReleasesExpiredRecoveryQuotaWithoutManualCleanup() => AssertAutomaticCleanupAsync(null);

    [PostgresFact]
    public async Task HostPostgresCleanup_ReleasesExpiredRecoveryQuotaWithoutManualCleanup()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        await AssertAutomaticCleanupAsync(database.ConnectionString);
    }

    private static async Task AssertAutomaticCleanupAsync(string? connection)
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = connection is null ? "Memory" : "Postgres",
            ["ConnectionStrings:OperationJournal"] = connection,
            ["OperationJournal:Capacity:MaxRecords"] = "2",
            ["OperationJournal:Capacity:MaxRecoveryRecords"] = "1",
            ["OperationJournal:Cleanup:RecoveryRetention"] = "01:00:00",
            ["OperationJournal:Cleanup:Interval"] = "00:00:01",
            ["OperationJournal:Cleanup:BatchSize"] = "1",
        });
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "probe");
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var message = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "probe",
            Kind = "http",
            Phase = "started",
            TraceId = "automatic-recovery-cleanup",
            HttpMethod = "POST",
            OccurredAt = now,
        };
        Assert.True((await journal.AppendAsync(message)).IsSuccess);
        Assert.True(await outbox.MarkDeadLetteredAsync(message.EventId, "stopped", now, 0));
        var request = new OperationJournalRecoveryRequest(Guid.NewGuid(), message.EventId, now, 0, "manual-retry");
        var actor = new OperationJournalRecoveryActor("test-operator", "test-host");
        var receipt = (await maintenance.RetryDeliveryAsync(request, actor)).Value;
        Assert.Equal(now.AddHours(1), receipt.RetainUntil);
        clock.UtcNow = receipt.RetainUntil;

        await app.StartAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await maintenance.GetRecoveryAsync(request.RequestId, timeout.Token)).IsSuccess)
        {
            await Task.Delay(25, timeout.Token);
        }
        Assert.Equal("Pending", (await maintenance.GetDeliveryAsync(message.EventId)).Value.State);
        Assert.True(await outbox.MarkDeadLetteredAsync(message.EventId, "stopped again", clock.UtcNow, 1));
        Assert.True((await maintenance.RetryDeliveryAsync(request with
        {
            RequestId = Guid.NewGuid(),
            ExpectedDeadLetteredAt = clock.UtcNow,
            ExpectedRetryRevision = 1,
        }, actor)).IsSuccess);
        await app.StopAsync();
    }

    [PostgresFact]
    public async Task CleanupCancellationAndDeleteFailure_PreserveReceiptAndQuota_ThenConcurrentCleanupCountsOnce()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock);
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 2, MaxRecoveryRecords = 1 },
            new() { RecoveryRetention = TimeSpan.FromHours(1), BatchSize = 1 });
        await using var app = services.BuildServiceProvider();
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var first = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "pricing",
            Kind = "http",
            Phase = "started",
            TraceId = "recovery-cleanup-fault",
            HttpMethod = "POST",
            OccurredAt = now,
        };
        var second = first with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid() };
        foreach (var message in new[] { first, second })
        {
            Assert.True((await journal.AppendAsync(message)).IsSuccess);
            Assert.True(await outbox.MarkDeadLetteredAsync(message.EventId, "stopped", now, 0));
        }
        var actor = new OperationJournalRecoveryActor("test-operator", "test-host");
        var request = new OperationJournalRecoveryRequest(Guid.NewGuid(), first.EventId, now, 0, "manual-retry");
        var refusedRequest = request with { RequestId = Guid.NewGuid(), MessageId = second.EventId };
        var receipt = (await maintenance.RetryDeliveryAsync(request, actor)).Value;
        clock.Advance(TimeSpan.FromHours(1));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var block = new NpgsqlCommand("LOCK TABLE operation_journal.recovery_records IN ACCESS EXCLUSIVE MODE", connection, transaction))
            { await block.ExecuteNonQueryAsync(); }
            using var cancellation = new CancellationTokenSource();
            var attempt = maintenance.CleanupRecoveryRecordsAsync(cancellation.Token);
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
        Assert.Equal(receipt, (await maintenance.GetRecoveryAsync(request.RequestId)).Value);
        Assert.Equal(OperationJournalRecoveryErrors.CapacityExceeded, (await maintenance.RetryDeliveryAsync(refusedRequest, actor)).Error);
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION operation_journal.reject_recovery_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected private cleanup failure'; END $$;
            CREATE TRIGGER reject_recovery_cleanup AFTER DELETE ON operation_journal.recovery_records
                FOR EACH STATEMENT EXECUTE FUNCTION operation_journal.reject_recovery_cleanup();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<DbUpdateException>(() => maintenance.CleanupRecoveryRecordsAsync());
        Assert.Equal(receipt, (await maintenance.GetRecoveryAsync(request.RequestId)).Value);
        Assert.Equal(OperationJournalRecoveryErrors.CapacityExceeded, (await maintenance.RetryDeliveryAsync(refusedRequest, actor)).Error);
        await using (var release = new NpgsqlCommand("DROP TRIGGER reject_recovery_cleanup ON operation_journal.recovery_records", connection))
        { await release.ExecuteNonQueryAsync(); }

        var counts = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var concurrent = app.CreateAsyncScope();
            return await concurrent.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>().CleanupRecoveryRecordsAsync();
        }));
        Assert.Equal(new[] { 0, 0, 1 }, counts.Order().ToArray());
        Assert.Equal(OperationJournalRecoveryErrors.NotFound, (await maintenance.GetRecoveryAsync(request.RequestId)).Error);
        Assert.Equal("Pending", (await maintenance.GetDeliveryAsync(first.EventId)).Value.State);
        Assert.True((await maintenance.RetryDeliveryAsync(refusedRequest, actor)).IsSuccess);
    }

    [PostgresFact]
    public async Task PostgresCleanup_RespectsReceiptDeadlineAndBatch_AndReleasesOnlyRecoveryQuota()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock);
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 3, MaxRecoveryRecords = 2 },
            new() { RecoveryRetention = TimeSpan.FromHours(1), BatchSize = 1 });
        await using var app = services.BuildServiceProvider();
        await AssertRetentionAsync(app, clock);
    }

    [Fact]
    public async Task MemoryCleanup_RespectsReceiptDeadlineAndBatch_AndReleasesOnlyRecoveryQuota()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock);
        services.AddOperationJournalMemoryStorage(new() { MaxRecords = 3, MaxRecoveryRecords = 2 },
            new() { RecoveryRetention = TimeSpan.FromHours(1), BatchSize = 1 });
        await using var app = services.BuildServiceProvider();
        await AssertRetentionAsync(app, clock);
    }

    private static async Task AssertRetentionAsync(ServiceProvider app, MutableClock clock)
    {
        var now = clock.UtcNow;
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var requests = new List<OperationJournalRecoveryRequest>();
        for (var i = 0; i < 3; i++)
        {
            var message = new OperationObservedV1
            {
                EventId = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                Source = "pricing",
                Kind = "http",
                Phase = "started",
                TraceId = "recovery-retention-test",
                HttpMethod = "POST",
                OccurredAt = now,
            };
            Assert.True((await journal.AppendAsync(message)).IsSuccess);
            Assert.True(await outbox.MarkDeadLetteredAsync(message.EventId, "stopped", now, 0));
            requests.Add(new(Guid.NewGuid(), message.EventId, now, 0, "manual-retry"));
        }
        var actor = new OperationJournalRecoveryActor("test-operator", "test-host");
        var first = (await maintenance.RetryDeliveryAsync(requests[0], actor)).Value;
        Assert.Equal(now.AddHours(1), first.RetainUntil);
        clock.Advance(TimeSpan.FromMinutes(30));
        var second = (await maintenance.RetryDeliveryAsync(requests[1], actor)).Value;
        Assert.Equal(now.AddMinutes(90), second.RetainUntil);
        Assert.Equal(OperationJournalRecoveryErrors.CapacityExceeded, (await maintenance.RetryDeliveryAsync(requests[2], actor)).Error);

        clock.UtcNow = first.RetainUntil.AddTicks(-1);
        Assert.Equal(0, await maintenance.CleanupRecoveryRecordsAsync());
        Assert.Equal(first, (await maintenance.GetRecoveryAsync(requests[0].RequestId)).Value);
        clock.UtcNow = first.RetainUntil;
        Assert.Equal(1, await maintenance.CleanupRecoveryRecordsAsync());
        Assert.Equal(OperationJournalRecoveryErrors.NotFound, (await maintenance.GetRecoveryAsync(requests[0].RequestId)).Error);
        Assert.Equal(second, (await maintenance.GetRecoveryAsync(requests[1].RequestId)).Value);
        Assert.Equal("Pending", (await maintenance.GetDeliveryAsync(requests[0].MessageId)).Value.State);
        Assert.Equal(OperationJournalDeliveryErrors.Conflict, (await maintenance.RetryDeliveryAsync(requests[0], actor)).Error);
        var third = (await maintenance.RetryDeliveryAsync(requests[2], actor)).Value;
        Assert.Equal(now.AddHours(2), third.RetainUntil);

        clock.UtcNow = third.RetainUntil;
        Assert.Equal(1, await maintenance.CleanupRecoveryRecordsAsync());
        Assert.Equal(OperationJournalRecoveryErrors.NotFound, (await maintenance.GetRecoveryAsync(requests[1].RequestId)).Error);
        Assert.Equal(third, (await maintenance.GetRecoveryAsync(requests[2].RequestId)).Value);
        Assert.Equal(1, await maintenance.CleanupRecoveryRecordsAsync());
        Assert.Equal(0, await maintenance.CleanupRecoveryRecordsAsync());
        foreach (var request in requests)
        {
            var delivery = (await maintenance.GetDeliveryAsync(request.MessageId)).Value;
            Assert.Equal("Pending", delivery.State);
            Assert.Equal(1, delivery.RetryRevision);
        }
    }
}
