using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class OperationJournalCleanupTests(JourneyDatabaseTemplates databases)
{
    [Theory]
    [InlineData("DeliveredRetention", "00:59:59")]
    [InlineData("DeliveredRetention", "31.00:00:00")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "1001")]
    [InlineData("Interval", "00:00:00.999")]
    [InlineData("Interval", "01:00:01")]
    [InlineData("Timeout", "00:00:00.049")]
    [InlineData("Timeout", "00:00:31")]
    public void InvalidCleanupPolicy_IsRejectedBeforeStarting(string key, string value)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
            ["OperationJournal:Cleanup:" + key] = value,
        });
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "probe"));
    }

    [PostgresFact]
    public async Task HostCleanup_TimeoutIsVisibleAndRecovers_WithoutClearingMissingObservations()
    {
        await using var database = await databases.CreateAsync("journal");
        var now = DateTimeOffset.UtcNow;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Postgres",
            ["ConnectionStrings:OperationJournal"] = database.ConnectionString,
            ["OperationJournal:Cleanup:Interval"] = "00:00:01",
            ["OperationJournal:Cleanup:Timeout"] = "00:00:00.250",
            ["OperationJournal:WriteTimeout"] = "00:00:00.050",
        });
        builder.Services.AddSingleton<IClock>(new FixedClock(now));
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "probe");
        await using var app = builder.Build();
        app.UseRouting();
        app.UseOperationJournal();
        app.MapGet("/work", () => Results.Ok());
        await using (var seed = app.Services.CreateAsyncScope())
        {
            var old = Started(now.AddDays(-3));
            Assert.True((await seed.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(old)).IsSuccess);
            await seed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
                .MarkDeliveredAsync(old.EventId, now.AddDays(-2));
        }
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await using var block = new NpgsqlCommand("LOCK TABLE operation_journal.outbox IN SHARE MODE", blocker, transaction);
            await block.ExecuteNonQueryAsync();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync("/work");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var status = app.Services.GetRequiredService<OperationJournalStatus>();
            while (!status.CleanupDegraded) { await Task.Delay(25, timeout.Token); }
            Assert.Equal(2, status.FailureCount);
            Assert.True(status.CleanupFailureCount > 0);
            await transaction.RollbackAsync();
        }
        var health = app.Services.GetRequiredService<HealthCheckService>();
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        HealthReport report;
        do
        {
            await Task.Delay(25, recovery.Token);
            report = await health.CheckHealthAsync(check => check.Name == "operation-journal", recovery.Token);
        } while ((bool)report.Entries["operation-journal"].Data["cleanupDegraded"]
            || (long)report.Entries["operation-journal"].Data["retainedRecords"] != 0);
        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal(2L, report.Entries["operation-journal"].Data["failedWrites"]);
        await app.StopAsync();
    }

    [PostgresFact]
    public async Task SourceCleanup_DoesNotEraseCentralObservationOrItsDuplicateAndConflictProtection()
    {
        await using var database = await databases.CreateAsync("journal");
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var now = DateTimeOffset.UtcNow;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        services.AddAuditingPostgresStorage(database.ConnectionString);
        services.AddScoped<OperationObservationIngestion>();
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var source = app.CreateAsyncScope();
        var journal = source.ServiceProvider.GetRequiredService<IOperationJournal>();
        var outbox = source.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observed = Started(now.AddDays(-3));
        Assert.True((await journal.AppendAsync(observed)).IsSuccess);
        var original = Assert.Single(await outbox.ReadPendingAsync(10, now));
        await using (var initial = app.CreateAsyncScope())
        { Assert.True(await initial.ServiceProvider.GetRequiredService<OperationObservationIngestion>().HandleAsync(original.ToEnvelope())); }
        await outbox.MarkDeliveredAsync(observed.EventId, now.AddDays(-2));
        Assert.Equal(1, await source.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>().CleanupDeliveredAsync());
        Assert.True((await journal.AppendAsync(observed)).IsSuccess);
        await using var replay = app.CreateAsyncScope();
        var consumer = replay.ServiceProvider.GetRequiredService<OperationObservationIngestion>();
        Assert.True(await consumer.HandleAsync(Assert.Single(await outbox.ReadPendingAsync(10, now)).ToEnvelope()));
        var changed = OutboxEntry.From(observed with { ActorId = "changed" }, new SystemTextJsonIntegrationEventSerializer());
        Assert.False(await consumer.HandleAsync(changed.ToEnvelope()));
        var page = await replay.ServiceProvider.GetRequiredService<IOperationObservationStore>().QueryAsync(new OperationQuery(1, 10));
        Assert.Equal(observed.OperationId, Assert.Single(page.Operations).OperationId);
    }

    [Fact]
    public async Task HostCleanup_AppliesConfiguredRetention_AndFreesCapacityWithoutManualCalls()
    {
        var now = DateTimeOffset.UtcNow;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
            ["OperationJournal:Capacity:MaxRecords"] = "2",
            ["OperationJournal:Cleanup:DeliveredRetention"] = "02:00:00",
            ["OperationJournal:Cleanup:BatchSize"] = "1",
        });
        builder.Services.AddSingleton<IClock>(new FixedClock(now));
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "probe");
        await using var app = builder.Build();
        var journal = app.Services.GetRequiredService<IOperationJournal>();
        var outbox = app.Services.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var expired = Started(now.AddDays(-3));
        var recent = Started(now.AddDays(-3));
        Assert.True((await journal.AppendAsync(expired)).IsSuccess);
        Assert.True((await journal.AppendAsync(recent)).IsSuccess);
        await outbox.MarkDeliveredAsync(expired.EventId, now.AddHours(-3));
        await outbox.MarkDeliveredAsync(recent.EventId, now.AddMinutes(-90));
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var health = app.Services.GetRequiredService<HealthCheckService>();
        while (true)
        {
            var report = await health.CheckHealthAsync(check => check.Name == "operation-journal", timeout.Token);
            if ((long)report.Entries["operation-journal"].Data["retainedRecords"] == 1) { break; }
            await Task.Delay(25, timeout.Token);
        }
        Assert.True((await journal.AppendAsync(recent)).IsSuccess);
        Assert.True((await journal.AppendAsync(Started(now))).IsSuccess);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started(now))).Error.Code);
        await app.StopAsync();
    }

    [PostgresFact]
    public async Task ConcurrentCleanup_ReleasesEachRecordOnce_AndConcurrentAppendStaysWithinCapacity()
    {
        await using var database = await databases.CreateAsync("journal");
        var now = DateTimeOffset.UtcNow;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 3 }, new() { BatchSize = 1 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var seed = app.CreateAsyncScope();
        var journal = seed.ServiceProvider.GetRequiredService<IOperationJournal>();
        var outbox = seed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        for (var index = 0; index < 2; index++)
        {
            var old = Started(now.AddDays(-3));
            Assert.True((await journal.AppendAsync(old)).IsSuccess);
            await outbox.MarkDeliveredAsync(old.EventId, now.AddDays(-2));
        }
        var pending = Started(now);
        Assert.True((await journal.AppendAsync(pending)).IsSuccess);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaners = Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = app.CreateAsyncScope();
            await release.Task;
            return await scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>().CleanupDeliveredAsync();
        }).ToArray();
        release.SetResult();
        var cleaned = await Task.WhenAll(cleaners);
        Assert.Equal(2, cleaned.Sum());
        Assert.All(cleaned, count => Assert.InRange(count, 0, 1));
        var writers = Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var scope = app.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(Started(now));
        }).ToArray();
        Assert.Equal(2, (await Task.WhenAll(writers)).Count(result => result.IsSuccess));
        var remaining = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, item => item.Id == pending.EventId);
    }

    [PostgresFact]
    public async Task CanceledCleanup_DoesNotReleaseCapacity_AndSameScopeCanRecover()
    {
        await using var database = await databases.CreateAsync("journal");
        var now = DateTimeOffset.UtcNow;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 1 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var observation = Started(now.AddDays(-3));
        Assert.True((await journal.AppendAsync(observation)).IsSuccess);
        await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
            .MarkDeliveredAsync(observation.EventId, now.AddDays(-2));
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var block = new NpgsqlCommand("LOCK TABLE operation_journal.outbox IN ACCESS EXCLUSIVE MODE", blocker, transaction))
        { await block.ExecuteNonQueryAsync(); }
        using var cancellation = new CancellationTokenSource();
        // A prior activity snapshot must not hide the cleanup's later relation-lock wait.
        await using (var initial = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity", blocker, transaction))
        {
            await initial.ExecuteScalarAsync();
        }
        var attempt = maintenance.CleanupDeliveredAsync(cancellation.Token);
        try
        {
            using var proof = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var waiting = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_locks
                        WHERE database = (SELECT oid FROM pg_database WHERE datname = current_database())
                          AND relation = 'operation_journal.outbox'::regclass AND NOT granted
                          AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))
                    """, blocker, transaction);
                if ((bool)(await waiting.ExecuteScalarAsync(proof.Token))!) { break; }
                Assert.False(attempt.IsCompleted);
                await Task.Delay(25, proof.Token);
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { cancellation.Cancel(); await transaction.RollbackAsync(); }
        Assert.True((await journal.AppendAsync(observation)).IsSuccess);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started(now))).Error.Code);
        Assert.Equal(1, await maintenance.CleanupDeliveredAsync());
        Assert.True((await journal.AppendAsync(Started(now))).IsSuccess);
    }

    [PostgresFact]
    public async Task CleanupFailureAfterDelete_RollsBackRecordsAndCapacity_AndCanRetry()
    {
        await using var database = await databases.CreateAsync("journal");
        var now = DateTimeOffset.UtcNow;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 1 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observation = Started(now.AddDays(-3));
        Assert.True((await journal.AppendAsync(observation)).IsSuccess);
        await outbox.MarkDeliveredAsync(observation.EventId, now.AddDays(-2));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION operation_journal.reject_release() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."RecordCount" < OLD."RecordCount" THEN RAISE EXCEPTION 'injected cleanup failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_release BEFORE UPDATE ON operation_journal.capacity
                FOR EACH ROW EXECUTE FUNCTION operation_journal.reject_release();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<PostgresException>(() => maintenance.CleanupDeliveredAsync());
        Assert.True((await journal.AppendAsync(observation)).IsSuccess);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started(now))).Error.Code);
        var health = await app.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Name == "operation-journal");
        Assert.Equal(1L, health.Entries["operation-journal"].Data["retainedRecords"]);
        await using (var restore = new NpgsqlCommand("DROP TRIGGER reject_release ON operation_journal.capacity; DROP FUNCTION operation_journal.reject_release();", connection))
        { await restore.ExecuteNonQueryAsync(); }
        Assert.Equal(1, await maintenance.CleanupDeliveredAsync());
        Assert.True((await journal.AppendAsync(Started(now))).IsSuccess);
    }

    [PostgresFact]
    public async Task PostgresCleanup_UsesDeliveryAgeAndBatchLimit_AndReleasesOnlyRemovedCapacity()
    {
        await using var database = await databases.CreateAsync("journal");
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 5 }, new() { BatchSize = 1 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await AssertCleanupAsync(app, now);
    }

    [Fact]
    public async Task MemoryCleanup_UsesDeliveryAgeAndBatchLimit_AndReleasesOnlyRemovedCapacity()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddOperationJournalMemoryStorage(new() { MaxRecords = 5 }, new() { BatchSize = 1 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await AssertCleanupAsync(app, now);
    }

    private static async Task AssertCleanupAsync(ServiceProvider app, DateTimeOffset now)
    {
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observations = Enumerable.Range(0, 5).Select(_ => Started(now.AddDays(-20))).ToArray();
        foreach (var observation in observations) { Assert.True((await journal.AppendAsync(observation)).IsSuccess); }
        await outbox.MarkDeliveredAsync(observations[0].EventId, now.AddDays(-2));
        await outbox.MarkDeliveredAsync(observations[0].EventId, now); // 重复确认不延长保留。
        await outbox.MarkDeliveredAsync(observations[1].EventId, now.AddDays(-1)); // 含截止时刻。
        await outbox.MarkDeliveredAsync(observations[2].EventId, now.AddDays(-1).AddTicks(10));
        await outbox.MarkDeadLetteredAsync(observations[3].EventId, "stopped", now.AddDays(-10), 0);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started(now))).Error.Code);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => maintenance.CleanupDeliveredAsync(canceled.Token));
        }
        Assert.Equal(1, await maintenance.CleanupDeliveredAsync());
        var health = await app.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Name == "operation-journal");
        Assert.Equal(4L, health.Entries["operation-journal"].Data["retainedRecords"]);
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var bytes = observations.Skip(1).Sum(item => (long)System.Text.Encoding.UTF8.GetByteCount(serializer.Serialize(item)));
        Assert.Equal(bytes, health.Entries["operation-journal"].Data["retainedPayloadBytes"]);
        await outbox.MarkFailedAsync(observations[0].EventId, "late-failure", now, 0);
        await outbox.MarkDeliveredAsync(observations[0].EventId, now);
        // 清理只收回来源去重窗口；重投用原身份，中央 Inbox 仍承担去重。
        Assert.True((await journal.AppendAsync(observations[0])).IsSuccess);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started(now))).Error.Code);
        Assert.Equal(1, await maintenance.CleanupDeliveredAsync());
        Assert.Equal(0, await maintenance.CleanupDeliveredAsync());
        Assert.True((await journal.AppendAsync(Started(now))).IsSuccess);
        Assert.True((await journal.AppendAsync(observations[2])).IsSuccess);
        Assert.True((await journal.AppendAsync(observations[3])).IsSuccess);
        Assert.True((await journal.AppendAsync(observations[4])).IsSuccess);
        var pending = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(3, pending.Count);
        Assert.Contains(pending, item => item.Id == observations[4].EventId);
    }

    private static OperationObservedV1 Started(DateTimeOffset now) => new()
    {
        EventId = Guid.NewGuid(),
        OperationId = Guid.NewGuid(),
        Source = "pricing",
        Kind = "http",
        Phase = "started",
        TraceId = "cleanup-test",
        HttpMethod = "POST",
        RouteTemplate = "/api/pricing/cost",
        OccurredAt = now,
    };
}
