using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingWebhookControl(PricingDbContext database, IPricingWebhookSender sender, IBackgroundExecutionObservation observations) :
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
        var delivery = await database.WebhookDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == command.EventId, cancellationToken).ConfigureAwait(false);
        if (delivery is null) { return Result.Failure<PricingWebhookStatus>(NotFound); }
        if (delivery.AttemptCount != command.ExpectedAttemptCount || delivery.State is not ("Failed" or "DeadLettered" or "Unknown"))
        {
            return Result.Failure<PricingWebhookStatus>(Conflict);
        }
        PricingWebhookQueryResult? receipt = null;
        if (delivery.State == "Unknown")
        {
            receipt = await observations.ObserveAsync(
                new RecoveryExecutionDescriptor("pricing.webhook.reconcile", "PricingWebhook", "guid", delivery.EventId.ToString("D")),
                () => Task.FromResult(new BackgroundExecutionInput<PricingWebhookQueryRequest>(
                    new(new Uri(delivery.Endpoint), delivery.EventId, delivery.TraceId), delivery.Execution)),
                request => sender.QueryAsync(request, cancellationToken),
                static result => result.Receipt == PricingWebhookReceipt.Unknown ? BackgroundExecutionOutcome.Deferred : BackgroundExecutionOutcome.Completed,
                cancellationToken).ConfigureAwait(false);
            if (receipt.Receipt == PricingWebhookReceipt.Unknown) { return Result.Failure<PricingWebhookStatus>(Unavailable); }
        }
        var accepted = receipt?.Receipt == PricingWebhookReceipt.Accepted;
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await PricingWebhookPersistence.LockAsync(database, command.EventId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.AttemptCount != command.ExpectedAttemptCount || current.State != delivery.State)
        {
            return Result.Failure<PricingWebhookStatus>(Conflict);
        }
        current.State = accepted ? "Succeeded" : "Pending";
        current.NextAttemptAt = null;
        current.DeliveredAt = accepted ? await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false) : null;
        current.LastError = null;
        current.LastStatusCode = accepted ? receipt?.StatusCode : null;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(ToStatus(current));
    }

    private static PricingWebhookStatus ToStatus(PricingWebhookDelivery delivery) => new(delivery.EventId, delivery.ItemId,
        delivery.State, delivery.AttemptCount, delivery.CreatedAt, delivery.NextAttemptAt, delivery.DeliveredAt,
        delivery.LastStatusCode, delivery.LastError, delivery.TraceId, delivery.CorrelationId, delivery.Execution);

    private static readonly Error Invalid = new("pricing.webhook.invalid", "通知标识或尝试次数无效。");
    private static readonly Error NotFound = new("pricing.webhook.not_found", "通知不存在。");
    private static readonly Error Conflict = new("pricing.webhook.conflict", "通知状态或尝试次数已改变。");
    private static readonly Error Unavailable = new("pricing.webhook.unavailable", "暂时无法核对接收结果，通知仍保持结果未知。");
}
