using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class OperationJournalRecoveryHealthTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PostgresRecoveryCapacity_IsVisibleBeforeRefusal_AndRecoversAfterCleanup()
    {
        await using var database = await databases.CreateAsync("journal");
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(clock);
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 2, MaxRecoveryRecords = 1 },
            new() { RecoveryRetention = TimeSpan.FromHours(1) });
        await using var app = services.BuildServiceProvider();
        await AssertRecoveryHealthAsync(app, clock);
    }

    [Fact]
    public async Task MemoryRecoveryCapacity_IsVisibleBeforeRefusal_AndRecoversAfterCleanup()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(clock);
        services.AddOperationJournalMemoryStorage(new() { MaxRecords = 2, MaxRecoveryRecords = 1 },
            new() { RecoveryRetention = TimeSpan.FromHours(1) });
        await using var app = services.BuildServiceProvider();
        await AssertRecoveryHealthAsync(app, clock);
    }

    private static async Task AssertRecoveryHealthAsync(ServiceProvider app, MutableClock clock)
    {
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var message = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "pricing",
            Kind = "http",
            Phase = "started",
            TraceId = "recovery-health",
            HttpMethod = "POST",
            OccurredAt = clock.UtcNow,
        };
        Assert.True((await journal.AppendAsync(message)).IsSuccess);
        Assert.True(await outbox.MarkDeadLetteredAsync(message.EventId, "stopped", clock.UtcNow, 0));
        Assert.True((await maintenance.RetryDeliveryAsync(new(Guid.NewGuid(), message.EventId, clock.UtcNow, 0, "manual-retry"),
            new("test-operator", "test-host"))).IsSuccess);
        var health = app.GetRequiredService<HealthCheckService>();

        var full = await health.CheckHealthAsync(check => check.Name == "operation-journal");

        Assert.Equal(HealthStatus.Degraded, full.Status);
        var data = full.Entries["operation-journal"].Data;
        Assert.Equal(1L, data["retainedRecoveryRecords"]);
        Assert.Equal(1, data["maxRecoveryRecords"]);
        Assert.Equal(true, data["recoveryCapacityReached"]);
        Assert.Equal(false, data["capacityReached"]);
        Assert.Equal(0L, data["failedWrites"]);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await maintenance.CleanupRecoveryRecordsAsync());
        var recovered = await health.CheckHealthAsync(check => check.Name == "operation-journal");
        Assert.Equal(HealthStatus.Healthy, recovered.Status);
        Assert.Equal(0L, recovered.Entries["operation-journal"].Data["retainedRecoveryRecords"]);
        Assert.Equal(false, recovered.Entries["operation-journal"].Data["recoveryCapacityReached"]);
        Assert.Equal(1L, recovered.Entries["operation-journal"].Data["retainedRecords"]);
    }
}
