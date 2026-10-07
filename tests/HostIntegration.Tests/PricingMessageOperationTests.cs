using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PricingMessageOperationTests
{
    [PostgresFact]
    public Task FailedMessageCommit_KeepsFailedObservation_AndRedeliveryCanCommit() => InterruptedMessageCommitAsync(cancel: false);

    [PostgresFact]
    public Task CanceledMessageCommit_KeepsCanceledObservation_AndRedeliveryCanCommit() => InterruptedMessageCommitAsync(cancel: true);

    private static async Task InterruptedMessageCommitAsync(bool cancel)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using (var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true))
        {
            await PricingDatabase.MigrateAsync(database.ConnectionString);
        }
        await using var app = TaskOperationTests.CreatePricingApp(database.ConnectionString, null);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var message = new CostCalculatedV1 { ItemId = Guid.NewGuid(), CostRevision = 1, UnitCost = 100m, OccurredAt = DateTimeOffset.UtcNow };
        var envelope = OutboxEntry.From(message, serializer).ToEnvelope();
        var consumer = services.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
        await using var barrier = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        await barrier.OpenAsync();
        var effect = cancel ? "PERFORM pg_advisory_xact_lock(640066);" : "RAISE EXCEPTION 'Injected message fact failure';";
        await using (var install = new NpgsqlCommand($"""
            SELECT pg_advisory_lock(640066);
            CREATE FUNCTION pricing.interrupt_message_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN {effect} RETURN NEW; END $$;
            CREATE TRIGGER interrupt_message_fact BEFORE INSERT ON pricing.outbox
            FOR EACH ROW EXECUTE FUNCTION pricing.interrupt_message_fact();
            """, barrier)) { await install.ExecuteNonQueryAsync(); }
        using var cancellation = new CancellationTokenSource();
        var pending = consumer.HandleAsync(envelope, cancellation.Token);
        try
        {
            if (cancel)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var blocked = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND @barrier = ANY(pg_blocking_pids(pid)))", barrier);
                blocked.Parameters.AddWithValue("barrier", barrier.ProcessID);
                while (!Equals(true, await blocked.ExecuteScalarAsync(budget.Token))) { await Task.Delay(20, budget.Token); }
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else { await Assert.ThrowsAsync<DbUpdateException>(() => pending); }
        }
        finally
        {
            await cancellation.CancelAsync();
            await using var restore = new NpgsqlCommand("SELECT pg_advisory_unlock(640066); DROP TRIGGER interrupt_message_fact ON pricing.outbox; DROP FUNCTION pricing.interrupt_message_fact()", barrier);
            await restore.ExecuteNonQueryAsync();
        }
        var sender = services.GetRequiredService<ISender>();
        Assert.True((await sender.QueryAsync(new GetPriceQuote(message.ItemId))).IsFailure);
        Assert.True((await sender.QueryAsync(new GetRecalculation(message.EventId))).IsFailure);
        Assert.Empty(await services.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow));
        var journal = services.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var failed = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal), item => item.Phase == "finished");
        Assert.Equal(cancel ? "canceled" : "failed", failed.Outcome);
        Assert.Equal("message", failed.Kind);
        Assert.Null(services.GetRequiredService<IExecutionContext>().Capture());
        Assert.True(await consumer.HandleAsync(envelope));
        var phases = await OperationEndpointInventoryTests.ReadAsync(journal);
        Assert.Equal(4, phases.Length);
        Assert.Contains(phases, item => item.OperationId == failed.OperationId && item.Outcome == failed.Outcome);
        var accepted = Assert.Single(phases, item => item.Outcome == "accepted");
        Assert.NotEqual(failed.OperationId, accepted.OperationId);
        var task = (await sender.QueryAsync(new GetRecalculation(message.EventId))).Value;
        Assert.Equal(accepted.OperationId, task.ExecutionOrigin!.OperationId);
        Assert.Equal(2, (await services.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task MessageConsumption_HasItsOwnSystemOperation_AndTaskAndFactsKeepTheFirstAcceptanceAcrossRestart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using (var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true))
        {
            await PricingDatabase.MigrateAsync(database.ConnectionString);
        }
        var parent = new ExecutionOrigin(Guid.NewGuid(), "costing", Guid.NewGuid(), "costing", "original-user", "cost-message-trace", "cost-message-correlation");
        var message = new CostCalculatedV1
        {
            ItemId = Guid.NewGuid(),
            CostRevision = 2,
            UnitCost = 100m,
            OccurredAt = DateTimeOffset.UtcNow,
            ExecutionOrigin = parent,
        };
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var envelope = OutboxEntry.From(message, serializer).ToEnvelope();
        Guid acceptedOperation;
        await using (var app = TaskOperationTests.CreatePricingApp(database.ConnectionString, "unrelated-ambient-user"))
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var consumer = services.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
            Assert.True(await consumer.HandleAsync(envelope));
            var journal = services.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var accepted = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal), item => item.Kind == "message" && item.Phase == "finished");
            acceptedOperation = accepted.OperationId;
            Assert.Equal("accepted", accepted.Outcome);
            Assert.Null(accepted.ActorId);
            Assert.Equal("pricing", accepted.Source);
            Assert.Equal("pricing.cost.accept", accepted.Metadata!.Action);
            Assert.Equal(CostCalculatedV1.Name, accepted.Metadata.SubjectType);
            Assert.Equal(envelope.MessageId.ToString("D"), accepted.Metadata.SubjectId);
            Assert.Equal("guid", accepted.Metadata.SubjectIdKind);
            Assert.Equal(parent.OperationId, accepted.Metadata.ParentOperationId);
            Assert.Equal(parent.RootOperationId, accepted.Metadata.RootOperationId);
            Assert.Equal(parent.InitiatorId, accepted.Metadata.InitiatorId);
            Assert.Null(accepted.Metadata.TaskId);
            Assert.Null(accepted.Metadata.TaskEpoch);
            var facts = (await services.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow))
                .Select(entry => serializer.Deserialize<PriceQuoteCommittedV1>(entry.Payload)).ToArray();
            Assert.Equal(2, facts.Length);
            Assert.All(facts, fact =>
            {
                Assert.Null(fact.ActorId);
                Assert.Equal(acceptedOperation, fact.Execution!.OperationId);
                Assert.Equal("pricing", fact.Execution.Source);
                Assert.Equal(parent.RootOperationId, fact.Execution.RootOperationId);
            });
            var quote = (await services.GetRequiredService<ISender>().QueryAsync(new GetPriceQuote(message.ItemId))).Value;
            Assert.Null(quote.Audit!.CreatedBy);
            Assert.True(await consumer.HandleAsync(envelope));
            Assert.True(await consumer.HandleAsync(OutboxEntry.From(message with { EventId = Guid.NewGuid(), CostRevision = 1 }, serializer).ToEnvelope()));
            Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { UnitCost = 101m }, serializer).ToEnvelope()));
            var attempts = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "message" && item.Phase == "finished").ToArray();
            Assert.Equal(new[] { "accepted", "duplicate", "rejected", "skipped" }, attempts.Select(item => item.Outcome).Order(StringComparer.Ordinal));
            Assert.Equal(4, attempts.Select(item => item.OperationId).Distinct().Count());
            Assert.Equal(2, (await services.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow)).Count);
            Assert.Null(services.GetRequiredService<IExecutionContext>().Capture());
        }
        await using var restarted = TaskOperationTests.CreatePricingApp(database.ConnectionString, "another-ambient-user");
        await using var work = restarted.Services.CreateAsyncScope();
        var sender = work.ServiceProvider.GetRequiredService<ISender>();
        var task = (await sender.QueryAsync(new GetRecalculation(envelope.MessageId))).Value;
        Assert.Equal(acceptedOperation, task.ExecutionOrigin!.OperationId);
        Assert.Equal("pricing", task.ExecutionOrigin.Source);
        Assert.Equal(parent.RootOperationId, task.ExecutionOrigin.RootOperationId);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        var completed = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(work.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)),
            item => item.Kind == "task" && item.Phase == "finished");
        Assert.Equal(acceptedOperation, completed.Metadata!.ParentOperationId);
        Assert.Equal("pricing", completed.Metadata.ParentSource);
        var result = Assert.Single((await work.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow))
            .Select(entry => serializer.Deserialize<PriceQuoteCommittedV1>(entry.Payload)), fact => fact.Operation == "result-applied");
        Assert.Null(result.ActorId);
        Assert.Equal(completed.OperationId, result.Execution!.OperationId);
        Assert.Null((await sender.QueryAsync(new GetPriceQuote(message.ItemId))).Value.Audit!.UpdatedBy);
    }
}
