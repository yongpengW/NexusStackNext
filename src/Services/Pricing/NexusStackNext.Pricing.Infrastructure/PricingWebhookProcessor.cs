using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Pricing.Contracts;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingWebhookProcessor(PricingDbContext database, PricingWebhookOptions options) : IIntegrationEventProcessor
{
    public string EventName => PricingRecalculationCompletedV1.Name;

    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        PricingRecalculationCompletedV1 message;
        try { message = new SystemTextJsonIntegrationEventSerializer().Deserialize<PricingRecalculationCompletedV1>(envelope.Payload); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId || message.ItemId == Guid.Empty || message.IdempotencyKey != message.EventId) { return false; }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        var inbox = new EfInboxStore<PricingDbContext>(database);
        if (!await inbox.TryBeginProcessingAsync("pricing-webhook", EventName, envelope.MessageId, now, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (!await database.WebhookDeliveries.AnyAsync(x => x.EventId == message.EventId, cancellationToken).ConfigureAwait(false))
        {
            database.WebhookDeliveries.Add(new PricingWebhookDelivery
            {
                EventId = message.EventId,
                ItemId = message.ItemId,
                Payload = envelope.Payload,
                Endpoint = options.Endpoint!,
                TraceId = message.TraceId,
                CorrelationId = message.CorrelationId,
                CreatedAt = now,
            });
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
