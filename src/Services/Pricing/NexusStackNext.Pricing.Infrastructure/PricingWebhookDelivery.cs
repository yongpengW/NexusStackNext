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
            PricingWebhookSendResult outcome;
            try
            {
                outcome = await observations.ObserveAsync(
                    new RecoveryExecutionDescriptor("pricing.webhook.deliver", "PricingWebhook", "guid", delivery.EventId.ToString("D")),
                    () => Task.FromResult(new BackgroundExecutionInput<PricingWebhookSendRequest>(
                        new(new Uri(delivery.Endpoint), delivery.Payload, delivery.EventId, delivery.TraceId), delivery.Execution)),
                    request => sender.SendAsync(request, cancellationToken),
                    static result => result.Succeeded ? BackgroundExecutionOutcome.Completed
                        : result.Retryable ? BackgroundExecutionOutcome.Deferred : BackgroundExecutionOutcome.Failed,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                database.ChangeTracker.Clear();
                var canceled = await database.WebhookDeliveries.SingleOrDefaultAsync(x => x.EventId == delivery.EventId, CancellationToken.None).ConfigureAwait(false);
                if (canceled is not null && canceled.State == "Pending")
                {
                    canceled.AttemptCount++;
                    canceled.State = "Unknown";
                    canceled.NextAttemptAt = null;
                    canceled.LastError = "canceled";
                    canceled.LastStatusCode = null;
                    await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            database.ChangeTracker.Clear();
            var current = await database.WebhookDeliveries.SingleOrDefaultAsync(x => x.EventId == delivery.EventId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.State != "Pending") { continue; }
            current.AttemptCount++;
            current.LastStatusCode = outcome.StatusCode;
            current.LastError = outcome.ErrorCode;
            if (outcome.Succeeded)
            {
                current.State = "Succeeded";
                current.DeliveredAt = clock.UtcNow;
                current.NextAttemptAt = null;
                delivered++;
            }
            else if (!outcome.Retryable || current.AttemptCount >= options.MaxAttempts)
            {
                current.State = outcome.Retryable ? "DeadLettered" : "Failed";
                current.NextAttemptAt = null;
            }
            else
            {
                current.NextAttemptAt = clock.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, current.AttemptCount)));
            }
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
                return new PricingWebhookSendResult(false, status == 408 || status == 429 || status >= 500, status, $"http_{status}");
            }
            try
            {
                var acknowledgement = await response.Content.ReadFromJsonAsync<PricingWebhookAcceptedV1>(cancellationToken).ConfigureAwait(false);
                return acknowledgement is { Accepted: true, EventId: var eventId, IdempotencyKey: var idempotencyKey }
                    && eventId == request.IdempotencyKey && idempotencyKey == request.IdempotencyKey
                    ? new PricingWebhookSendResult(true, false, status, null)
                    : new PricingWebhookSendResult(false, false, status, "invalid_ack");
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or NotSupportedException)
            {
                return new PricingWebhookSendResult(false, false, status, "invalid_ack");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PricingWebhookSendResult(false, true, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new PricingWebhookSendResult(false, true, null, "transport_unavailable");
        }
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
