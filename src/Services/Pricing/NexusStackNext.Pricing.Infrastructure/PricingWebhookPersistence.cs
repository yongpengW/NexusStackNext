using Microsoft.EntityFrameworkCore;

namespace NexusStackNext.Pricing.Infrastructure;

internal static class PricingWebhookPersistence
{
    internal static Task<PricingWebhookDelivery?> LockAsync(PricingDbContext database, Guid eventId, CancellationToken cancellationToken)
        => database.WebhookDeliveries.FromSqlInterpolated(
            $"SELECT * FROM pricing.webhook_deliveries WHERE \"EventId\" = {eventId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
}
