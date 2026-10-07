using Microsoft.Extensions.DependencyInjection;
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
public sealed class OperationJournalRecoveryTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PostgresRecovery_UsesSeparateBoundedQuotaAndNeverOverwritesEvidence()
    {
        await using var database = await databases.CreateAsync("journal");
        var services = new ServiceCollection();
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 2, MaxRecoveryRecords = 1 });
        await using var app = services.BuildServiceProvider();
        await AssertRecoveryQuotaAsync(app);
    }

    [Fact]
    public async Task MemoryRecovery_UsesSeparateBoundedQuotaAndNeverOverwritesEvidence()
    {
        var services = new ServiceCollection();
        services.AddOperationJournalMemoryStorage(new() { MaxRecords = 2, MaxRecoveryRecords = 1 });
        await using var app = services.BuildServiceProvider();
        await AssertRecoveryQuotaAsync(app);
    }

    private static async Task AssertRecoveryQuotaAsync(ServiceProvider app)
    {
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var first = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "pricing",
            Kind = "http",
            Phase = "started",
            TraceId = "recovery-quota-test",
            HttpMethod = "POST",
            OccurredAt = now,
        };
        var second = first with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid() };
        foreach (var message in new[] { first, second })
        {
            Assert.True((await journal.AppendAsync(message)).IsSuccess);
            Assert.True(await outbox.MarkDeadLetteredAsync(message.EventId, "stopped", now, 0));
        }
        Assert.Equal("operation_journal.capacity_exceeded",
            (await journal.AppendAsync(first with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid() })).Error.Code);
        var actor = new OperationJournalRecoveryActor("test-operator", "test-host");
        var request = new OperationJournalRecoveryRequest(Guid.NewGuid(), first.EventId, now, 0, "manual-retry");
        var secondRequest = request with { RequestId = Guid.NewGuid(), MessageId = second.EventId };
        var results = await Task.WhenAll(new[] { request, secondRequest }.Select(candidate => Task.Run(async () =>
        {
            await using var concurrent = app.CreateAsyncScope();
            var result = await concurrent.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>().RetryDeliveryAsync(candidate, actor);
            return (Request: candidate, Result: result);
        })));

        var winner = Assert.Single(results, item => item.Result.IsSuccess);
        var refused = Assert.Single(results, item => item.Result.IsFailure);
        var receipt = winner.Result.Value;
        Assert.Equal(OperationJournalRecoveryErrors.CapacityExceeded, refused.Result.Error);
        Assert.Equal(OperationJournalRecoveryErrors.NotFound, (await maintenance.GetRecoveryAsync(refused.Request.RequestId)).Error);
        var stopped = (await maintenance.GetDeliveryAsync(refused.Request.MessageId)).Value;
        Assert.Equal("DeadLettered", stopped.State);
        Assert.Equal(0, stopped.RetryRevision);
        Assert.Equal(receipt, (await maintenance.RetryDeliveryAsync(winner.Request, actor)).Value);
        Assert.Equal(receipt, (await maintenance.GetRecoveryAsync(winner.Request.RequestId)).Value);
    }

    [PostgresFact]
    public async Task PostgresRecovery_ReplaysOneReceiptEvenAfterDeliveryAndSourceCleanup()
    {
        await using var database = await databases.CreateAsync("journal");
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(now.AddDays(2).AddTicks(7)));
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 1 });
        await using var app = services.BuildServiceProvider();
        var receipt = await AssertReceiptReplayAsync(app, now);
        await app.DisposeAsync();
        var reopenedServices = new ServiceCollection();
        reopenedServices.AddOperationJournalPostgresStorage(database.ConnectionString);
        await using var reopened = reopenedServices.BuildServiceProvider();
        await using var read = reopened.CreateAsyncScope();
        Assert.Equal(receipt, (await read.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>()
            .GetRecoveryAsync(receipt.Request.RequestId)).Value);
    }

    [Fact]
    public async Task MemoryRecovery_ReplaysOneReceiptEvenAfterDeliveryAndSourceCleanup()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(now.AddDays(2)));
        services.AddOperationJournalMemoryStorage(new() { MaxRecords = 1 });
        await using var app = services.BuildServiceProvider();
        await AssertReceiptReplayAsync(app, now);
    }

    private static async Task<OperationJournalRecoveryReceipt> AssertReceiptReplayAsync(ServiceProvider app, DateTimeOffset now)
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
            TraceId = "recovery-test",
            HttpMethod = "POST",
            OccurredAt = now,
        };
        Assert.True((await journal.AppendAsync(message)).IsSuccess);
        Assert.True(await outbox.MarkDeadLetteredAsync(message.EventId, "private failure", now, 0));
        var request = new OperationJournalRecoveryRequest(Guid.NewGuid(), message.EventId, now, 0, "dependency-restored");
        var actor = new OperationJournalRecoveryActor("test-operator", "test-host");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await using var concurrent = app.CreateAsyncScope();
            return await concurrent.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>().RetryDeliveryAsync(request, actor);
        })));

        Assert.All(attempts, attempt => Assert.True(attempt.IsSuccess));
        var receipt = Assert.Single(attempts.Select(attempt => attempt.Value).Distinct());
        Assert.Equal(request, receipt.Request);
        Assert.Equal(actor, receipt.Actor);
        Assert.Equal("pricing", receipt.Source);
        Assert.Equal(now.AddDays(2), receipt.RecoveredAt);
        Assert.Equal(1, receipt.RetryRevision);
        Assert.Equal(receipt, (await maintenance.GetRecoveryAsync(request.RequestId)).Value);
        Assert.Equal(receipt, (await maintenance.RetryDeliveryAsync(request, actor)).Value);
        Assert.Equal(1, (await maintenance.GetDeliveryAsync(message.EventId)).Value.RetryRevision);

        await outbox.MarkDeliveredAsync(message.EventId, now);
        Assert.Equal(1, await maintenance.CleanupDeliveredAsync());
        Assert.Equal(OperationJournalDeliveryErrors.NotFound, (await maintenance.GetDeliveryAsync(message.EventId)).Error);
        Assert.Equal(receipt, (await maintenance.GetRecoveryAsync(request.RequestId)).Value);
        Assert.Equal(receipt, (await maintenance.RetryDeliveryAsync(request, actor)).Value);
        Assert.Equal(OperationJournalRecoveryErrors.RequestConflict,
            (await maintenance.RetryDeliveryAsync(request with { Reason = "manual-retry" }, actor)).Error);
        Assert.Equal(OperationJournalRecoveryErrors.RequestConflict,
            (await maintenance.RetryDeliveryAsync(request, actor with { Account = "another-operator" })).Error);
        return receipt;
    }
}
