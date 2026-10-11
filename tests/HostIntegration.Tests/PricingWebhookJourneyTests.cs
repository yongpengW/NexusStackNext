using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.WebhookProcessHost;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingWebhookJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Persisted_external_effect_is_reconciled_after_both_real_processes_crash_without_resending()
    {
        await using var database = await databases.CreateAsync("pricing");
        var eventId = Guid.NewGuid();
        await SeedWebhookAsync(database.ConnectionString, new PricingRecalculationCompletedV1
        {
            EventId = eventId,
            IdempotencyKey = eventId,
            ItemId = Guid.NewGuid(),
            Version = 1,
            InputRevision = 1,
            CalculatedRevision = 1,
            TraceId = "trace-process-loss",
            CorrelationId = "correlation-process-loss",
            OccurredAt = DateTimeOffset.UtcNow,
        });
        var assembly = typeof(WebhookProcessMarker).Assembly.Location;
        var path = Path.Combine(Path.GetTempPath(), $"nsn-webhook-receipt-{Guid.NewGuid():N}.json");
        var receiverSettings = new Dictionary<string, string>
        {
            ["NSN_WEBHOOK_PROBE_MODE"] = "receiver",
            ["NSN_WEBHOOK_PROBE_STATE"] = path,
            ["NSN_WEBHOOK_PROBE_HOLD_REPLY"] = "true",
        };
        BusinessProcess? receiver = null;
        BusinessProcess? sender = null;
        try
        {
            receiver = await BusinessProcess.StartAsync(assembly, "Pricing", database.ConnectionString, settings: receiverSettings);
            sender = await BusinessProcess.StartAsync(assembly, "Pricing", database.ConnectionString, settings: new Dictionary<string, string>
            {
                ["NSN_WEBHOOK_PROBE_RECEIVER"] = receiver.Client.BaseAddress!.AbsoluteUri,
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var receipt = await receiver.Client.GetFromJsonAsync<ReceiverReceipt>(new Uri($"/stats/{eventId:D}", UriKind.Relative), timeout.Token);
                if (receipt!.Accepted)
                {
                    Assert.Equal(1, receipt.Requests);
                    Assert.Equal(0, receipt.Queries);
                    break;
                }
                await Task.Delay(100, timeout.Token);
            }
            var inFlight = await sender.Client.GetFromJsonAsync<PricingWebhookStatus>(new Uri($"/status/{eventId:D}", UriKind.Relative));
            Assert.Equal("Unknown", inFlight!.State);
            Assert.Equal(1, inFlight.AttemptCount);
            Assert.Equal("awaiting_confirmation", inFlight.LastError);
            await sender.CrashAsync();
            await receiver.CrashAsync();
            await sender.DisposeAsync();
            sender = null;
            await receiver.DisposeAsync();
            receiver = null;

            receiverSettings["NSN_WEBHOOK_PROBE_HOLD_REPLY"] = "false";
            receiver = await BusinessProcess.StartAsync(assembly, "Pricing", database.ConnectionString, settings: receiverSettings);
            sender = await BusinessProcess.StartAsync(assembly, "Pricing", database.ConnectionString, settings: new Dictionary<string, string>
            {
                ["NSN_WEBHOOK_PROBE_RECEIVER"] = receiver.Client.BaseAddress!.AbsoluteUri,
            });
            var retained = await sender.Client.GetFromJsonAsync<PricingWebhookStatus>(new Uri($"/status/{eventId:D}", UriKind.Relative));
            Assert.Equal("Unknown", retained!.State);
            Assert.Equal(1, retained.AttemptCount);
            Assert.Equal("awaiting_confirmation", retained.LastError);
            using var response = await sender.Client.PostAsJsonAsync(new Uri($"/retry/{eventId:D}", UriKind.Relative), new { expectedAttemptCount = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var reconciled = await response.Content.ReadFromJsonAsync<PricingWebhookStatus>();
            Assert.Equal("Succeeded", reconciled!.State);
            Assert.Equal(1, reconciled.AttemptCount);
            Assert.NotNull(reconciled.DeliveredAt);
            var final = await receiver.Client.GetFromJsonAsync<ReceiverReceipt>(new Uri($"/stats/{eventId:D}", UriKind.Relative));
            Assert.Equal(1, final!.Requests);
            Assert.Equal(1, final.Queries);
            Assert.True(final.Accepted);
        }
        finally
        {
            if (sender is not null) { await sender.DisposeAsync(); }
            if (receiver is not null) { await receiver.DisposeAsync(); }
            File.Delete(path);
        }
    }

    [PostgresFact]
    public async Task Webhook_status_requires_permission_and_survives_pricing_process_restart()
    {
        await using var database = await databases.CreateAsync("pricing");
        var eventId = Guid.NewGuid();
        var eventMessage = new PricingRecalculationCompletedV1
        {
            EventId = eventId,
            IdempotencyKey = eventId,
            ItemId = Guid.NewGuid(),
            Version = 1,
            InputRevision = 1,
            CalculatedRevision = 1,
            TraceId = "trace-http",
            CorrelationId = "correlation-http",
            OccurredAt = DateTimeOffset.UtcNow,
        };
        await SeedWebhookAsync(database.ConnectionString, eventMessage);

        var assembly = typeof(PricingHostMarker).Assembly.Location;
        var settings = new Dictionary<string, string>
        {
            ["Pricing__Webhook__Enabled"] = "false",
            ["Pricing__Messaging__Enabled"] = "false",
            ["Pricing__Worker__Enabled"] = "false",
        };
        await using var app = await BusinessProcess.StartAsync(assembly, "Pricing", database.ConnectionString, settings: settings);
        var path = new Uri($"/api/pricing/webhooks/{eventId:D}", UriKind.Relative);

        using (var anonymous = await app.Client.GetAsync(path)) { Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); }
        app.Authenticate(root: false);
        using (var ordinary = await app.Client.GetAsync(path)) { Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode); }
        app.Authenticate();
        using (var authorized = await app.Client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            Assert.Contains(eventId.ToString("D"), await authorized.Content.ReadAsStringAsync());
        }

        await app.CrashAsync();
        await using var restarted = await BusinessProcess.StartAsync(assembly, "Pricing", database.ConnectionString, settings: settings);
        restarted.Authenticate();
        using var afterRestart = await restarted.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, afterRestart.StatusCode);
        Assert.Contains(eventId.ToString("D"), await afterRestart.Content.ReadAsStringAsync());
    }

    private static async Task SeedWebhookAsync(string connection, PricingRecalculationCompletedV1 message)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddPricingPostgres(connection,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        await using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var envelope = new EventEnvelope
        {
            MessageId = message.EventId,
            EventName = message.EventName,
            Payload = serializer.Serialize(message),
            OccurredAt = message.OccurredAt,
        };
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(PricingRecalculationCompletedV1.Name);
        Assert.True(await processor.HandleAsync(envelope));
    }

    private sealed record ReceiverReceipt(int Requests, int Queries, bool Accepted);
}
