using System.Net;
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

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingWebhookJourneyTests(JourneyDatabaseTemplates databases)
{
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
}
