using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingWebhookOutboxTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Completed_recalculation_adds_one_minimal_webhook_event_to_the_same_outbox()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);

        Assert.True((await sender.SendAsync(request)).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);

        var entries = await scope.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var webhook = Assert.Single(entries, entry => entry.EventName == PricingRecalculationCompletedV1.Name);
        var message = serializer.Deserialize<PricingRecalculationCompletedV1>(webhook.Payload);

        Assert.Equal(webhook.Id, message.EventId);
        Assert.Equal(message.EventId, message.IdempotencyKey);
        Assert.Equal(request.ItemId, message.ItemId);
        Assert.Equal(1, message.InputRevision);
        Assert.Equal(1, message.CalculatedRevision);
        Assert.DoesNotContain("80", webhook.Payload);
        Assert.DoesNotContain("0.2", webhook.Payload);
    }

    [PostgresFact]
    public async Task Webhook_event_is_materialized_once_and_can_be_read_by_its_id()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var eventId = Guid.NewGuid();
        var message = new PricingRecalculationCompletedV1
        {
            EventId = eventId,
            IdempotencyKey = eventId,
            ItemId = Guid.NewGuid(),
            Version = 4,
            InputRevision = 3,
            CalculatedRevision = 3,
            TraceId = "trace-webhook",
            CorrelationId = "correlation-webhook",
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var envelope = new EventEnvelope
        {
            MessageId = message.EventId,
            EventName = message.EventName,
            Payload = serializer.Serialize(message),
            OccurredAt = message.OccurredAt,
        };
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(PricingRecalculationCompletedV1.Name);

        Assert.True(await processor.HandleAsync(envelope));
        Assert.True(await processor.HandleAsync(envelope));

        var status = await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPricingWebhook(message.EventId));
        Assert.True(status.IsSuccess);
        Assert.Equal("Pending", status.Value.State);
        Assert.Equal(message.ItemId, status.Value.ItemId);
        Assert.Equal(0, status.Value.AttemptCount);
    }

    [PostgresFact]
    public async Task Successful_delivery_uses_one_stable_idempotency_key_and_is_not_sent_again()
    {
        var sender = new RecordingWebhookSender();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        services.AddSingleton<IPricingWebhookSender>(sender);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var eventId = Guid.NewGuid();
        var message = new PricingRecalculationCompletedV1
        {
            EventId = eventId,
            IdempotencyKey = eventId,
            ItemId = Guid.NewGuid(),
            Version = 4,
            InputRevision = 3,
            CalculatedRevision = 3,
            TraceId = "trace-delivery",
            CorrelationId = "correlation-delivery",
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var envelope = new EventEnvelope
        {
            MessageId = eventId,
            EventName = message.EventName,
            Payload = serializer.Serialize(message),
            OccurredAt = message.OccurredAt,
        };
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(PricingRecalculationCompletedV1.Name);
        Assert.True(await processor.HandleAsync(envelope));

        var runner = scope.ServiceProvider.GetRequiredService<IPricingWebhookDeliveryRunner>();
        Assert.Equal(1, await runner.DeliverPendingAsync());
        Assert.Equal(0, await runner.DeliverPendingAsync());
        Assert.Equal(eventId, Assert.Single(sender.Keys));

        var status = await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPricingWebhook(eventId));
        Assert.True(status.IsSuccess);
        Assert.Equal("Succeeded", status.Value.State);
        Assert.Equal(1, status.Value.AttemptCount);
    }

    private sealed class RecordingWebhookSender : IPricingWebhookSender
    {
        public List<Guid> Keys { get; } = [];

        public Task<PricingWebhookSendResult> SendAsync(PricingWebhookSendRequest request, CancellationToken cancellationToken = default)
        {
            Keys.Add(request.IdempotencyKey);
            return Task.FromResult(new PricingWebhookSendResult(true, false, 202, null));
        }
    }
}
