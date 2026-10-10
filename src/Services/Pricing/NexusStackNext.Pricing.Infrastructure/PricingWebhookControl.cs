using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingWebhookControl(PricingDbContext database) :
    IQueryHandler<GetPricingWebhook, PricingWebhookStatus>, ICommandHandler<RetryPricingWebhook, PricingWebhookStatus>
{
    public async Task<Result<PricingWebhookStatus>> HandleAsync(GetPricingWebhook query, CancellationToken cancellationToken = default)
    {
        if (query.EventId == Guid.Empty) { return Result.Failure<PricingWebhookStatus>(Invalid); }
        var delivery = await database.WebhookDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == query.EventId, cancellationToken).ConfigureAwait(false);
        return delivery is null ? Result.Failure<PricingWebhookStatus>(NotFound) : Result.Success(ToStatus(delivery));
    }

    public async Task<Result<PricingWebhookStatus>> HandleAsync(RetryPricingWebhook command, CancellationToken cancellationToken = default)
    {
        if (command.EventId == Guid.Empty || command.ExpectedAttemptCount < 0) { return Result.Failure<PricingWebhookStatus>(Invalid); }
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var delivery = await database.WebhookDeliveries.SingleOrDefaultAsync(x => x.EventId == command.EventId, cancellationToken).ConfigureAwait(false);
        if (delivery is null) { return Result.Failure<PricingWebhookStatus>(NotFound); }
        if (delivery.AttemptCount != command.ExpectedAttemptCount || delivery.State is not ("Failed" or "DeadLettered" or "Unknown"))
        {
            return Result.Failure<PricingWebhookStatus>(Conflict);
        }
        delivery.State = "Pending";
        delivery.NextAttemptAt = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        delivery.LastError = null;
        delivery.LastStatusCode = null;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(ToStatus(delivery));
    }

    private static PricingWebhookStatus ToStatus(PricingWebhookDelivery delivery) => new(delivery.EventId, delivery.ItemId,
        delivery.State, delivery.AttemptCount, delivery.CreatedAt, delivery.NextAttemptAt, delivery.DeliveredAt,
        delivery.LastStatusCode, delivery.LastError, delivery.TraceId, delivery.CorrelationId, delivery.Execution);

    private static readonly Error Invalid = new("pricing.webhook.invalid", "通知标识或尝试次数无效。");
    private static readonly Error NotFound = new("pricing.webhook.not_found", "通知不存在。");
    private static readonly Error Conflict = new("pricing.webhook.conflict", "通知状态或尝试次数已改变。");
}
