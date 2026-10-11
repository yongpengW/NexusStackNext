using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingWebhookDelivery
{
    public Guid EventId { get; set; }
    public required Guid ItemId { get; set; }
    public required string Payload { get; set; }
    public required string Endpoint { get; set; }
    public required string TraceId { get; set; }
    public required string CorrelationId { get; set; }
    public ExecutionOrigin? Execution { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string State { get; set; } = "Pending";
    public string? LastError { get; set; }
    public int? LastStatusCode { get; set; }
}

internal sealed class PricingWebhookDeliveryService(PricingDbContext database, PricingWebhookOptions options,
    IPricingWebhookSender sender, IClock clock, IBackgroundExecutionObservation observations) : IPricingWebhookDeliveryRunner
{
    public async Task<int> DeliverPendingAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) { return 0; }
        var now = clock.UtcNow;
        var pending = await database.WebhookDeliveries.AsNoTracking()
            .Where(x => x.State == "Pending" && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.EventId).Take(20).ToListAsync(cancellationToken).ConfigureAwait(false);
        var delivered = 0;
        foreach (var delivery in pending)
        {
            var attempt = delivery.AttemptCount + 1;
            database.ChangeTracker.Clear();
            await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                var claimed = await PricingWebhookPersistence.LockAsync(database, delivery.EventId, cancellationToken).ConfigureAwait(false);
                if (claimed is null || claimed.State != "Pending" || claimed.AttemptCount != delivery.AttemptCount) { continue; }
                claimed.State = "Unknown";
                claimed.AttemptCount = attempt;
                claimed.NextAttemptAt = null;
                claimed.LastError = "awaiting_confirmation";
                claimed.LastStatusCode = null;
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            PricingWebhookSendResult outcome;
            try
            {
                outcome = await observations.ObserveAsync(
                    new RecoveryExecutionDescriptor("pricing.webhook.deliver", "PricingWebhook", "guid", delivery.EventId.ToString("D")),
                    () => Task.FromResult(new BackgroundExecutionInput<PricingWebhookSendRequest>(
                        new(new Uri(delivery.Endpoint), delivery.Payload, delivery.EventId, delivery.TraceId), delivery.Execution)),
                    request => sender.SendAsync(request, cancellationToken),
                    static result => result.Succeeded ? BackgroundExecutionOutcome.Completed
                        : result.Retryable || result.OutcomeUnknown ? BackgroundExecutionOutcome.Deferred : BackgroundExecutionOutcome.Failed,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                database.ChangeTracker.Clear();
                await using var transaction = await database.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                var canceled = await PricingWebhookPersistence.LockAsync(database, delivery.EventId, CancellationToken.None).ConfigureAwait(false);
                if (canceled is not null && canceled.State == "Unknown" && canceled.AttemptCount == attempt)
                {
                    canceled.LastError = "canceled";
                    await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                    await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            string state;
            DateTimeOffset? nextAttemptAt = null;
            DateTimeOffset? deliveredAt = null;
            if (outcome.Succeeded)
            {
                state = "Succeeded";
                deliveredAt = clock.UtcNow;
            }
            else if (outcome.OutcomeUnknown)
            {
                state = "Unknown";
            }
            else if (!outcome.Retryable || attempt >= options.MaxAttempts)
            {
                state = outcome.Retryable ? "DeadLettered" : "Failed";
            }
            else
            {
                state = "Pending";
                var delay = outcome.RetryAfter is { } retryAfter
                    && retryAfter >= TimeSpan.Zero
                    && retryAfter <= PricingWebhookDeliveryPolicy.MaxRetryAfter
                    ? retryAfter
                    : TimeSpan.FromSeconds(Math.Min(PricingWebhookDeliveryPolicy.MaxRetryAfter.TotalSeconds,
                        Math.Pow(2, attempt)));
                nextAttemptAt = clock.UtcNow.Add(delay);
            }
            database.ChangeTracker.Clear();
            await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                var completed = await PricingWebhookPersistence.LockAsync(database, delivery.EventId, cancellationToken).ConfigureAwait(false);
                if (completed is null || completed.State != "Unknown" || completed.AttemptCount != attempt) { continue; }
                completed.State = state;
                completed.DeliveredAt = deliveredAt;
                completed.NextAttemptAt = nextAttemptAt;
                completed.LastError = outcome.ErrorCode;
                completed.LastStatusCode = outcome.StatusCode;
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (outcome.Succeeded) { delivered++; }
            }
        }
        return delivered;
    }

}

internal sealed class HttpPricingWebhookSender(HttpClient client) : IPricingWebhookSender
{
    public async Task<PricingWebhookSendResult> SendAsync(PricingWebhookSendRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
            {
                Content = new StringContent(request.Payload, System.Text.Encoding.UTF8, "application/json"),
            };
            message.Headers.Add("Idempotency-Key", request.IdempotencyKey.ToString("D"));
            message.Headers.Add("X-NexusStack-Trace", request.TraceId);
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var retryable = status == 429;
                return new PricingWebhookSendResult(false, retryable, status, $"http_{status}", retryable ? ParseRetryAfter(response) : null,
                    OutcomeUnknown: status == 408 || status >= 500);
            }
            try
            {
                var acknowledgement = await response.Content.ReadFromJsonAsync<PricingWebhookAcceptedV1>(cancellationToken).ConfigureAwait(false);
                return acknowledgement is { Accepted: true, EventId: var eventId, IdempotencyKey: var idempotencyKey }
                    && eventId == request.IdempotencyKey && idempotencyKey == request.IdempotencyKey
                    ? new PricingWebhookSendResult(true, false, status, null)
                    : new PricingWebhookSendResult(false, false, status, "invalid_ack", OutcomeUnknown: true);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or NotSupportedException)
            {
                return new PricingWebhookSendResult(false, false, status, "invalid_ack", OutcomeUnknown: true);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PricingWebhookSendResult(false, false, null, "timeout", OutcomeUnknown: true);
        }
        catch (HttpRequestException)
        {
            return new PricingWebhookSendResult(false, false, null, "transport_unavailable", OutcomeUnknown: true);
        }
    }

    public async Task<PricingWebhookQueryResult> QueryAsync(PricingWebhookQueryRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, request.Endpoint);
            message.Headers.Add("Idempotency-Key", request.IdempotencyKey.ToString("D"));
            message.Headers.Add("X-NexusStack-Trace", request.TraceId);
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode != System.Net.HttpStatusCode.OK) { return new(PricingWebhookReceipt.Unknown, status, $"http_{status}"); }
            try
            {
                var acknowledgement = await response.Content.ReadFromJsonAsync<PricingWebhookAcceptedV1>(cancellationToken).ConfigureAwait(false);
                return acknowledgement is not null && acknowledgement.EventId == request.IdempotencyKey
                    && acknowledgement.IdempotencyKey == request.IdempotencyKey
                    ? new(acknowledgement.Accepted ? PricingWebhookReceipt.Accepted : PricingWebhookReceipt.NotAccepted, status, null)
                    : new(PricingWebhookReceipt.Unknown, status, "invalid_ack");
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or NotSupportedException)
            {
                return new(PricingWebhookReceipt.Unknown, status, "invalid_ack");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(PricingWebhookReceipt.Unknown, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new(PricingWebhookReceipt.Unknown, null, "transport_unavailable");
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) { return null; }
        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) { return null; }
        if (raw.All(static character => character is >= '0' and <= '9')
            && int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds >= 0 && seconds <= PricingWebhookDeliveryPolicy.MaxRetryAfter.TotalSeconds
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
        if (!DateTimeOffset.TryParseExact(raw, "R", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date))
        { return null; }
        var delay = date - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) { return TimeSpan.Zero; }
        return delay <= PricingWebhookDeliveryPolicy.MaxRetryAfter ? delay : null;
    }
}

internal sealed partial class PricingWebhookWorker(IServiceScopeFactory scopes, PricingWebhookOptions options,
    ILogger<PricingWebhookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                _ = await scope.ServiceProvider.GetRequiredService<IPricingWebhookDeliveryRunner>()
                    .DeliverPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { LogDeliveryRoundFailed(logger, error); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Pricing Webhook 投递循环暂时失败，将在下一轮继续。")]
    private static partial void LogDeliveryRoundFailed(ILogger logger, Exception error);
}

internal static class PricingWebhookDeliveryPolicy
{
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(300);
}
