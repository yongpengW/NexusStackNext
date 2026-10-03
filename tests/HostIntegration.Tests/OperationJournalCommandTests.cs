using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PlatformHost;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationJournalCommandTests
{
    [PostgresFact]
    public async Task ShowDelivery_ReadsPersistentSafeStateWithoutStartingTheBusinessHost()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var services = new ServiceCollection();
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        await using var app = services.BuildServiceProvider();
        await using var scope = app.CreateAsyncScope();
        var message = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "pricing",
            Kind = "http",
            Phase = "started",
            TraceId = "private-payload-marker",
            HttpMethod = "POST",
            RouteTemplate = "/api/pricing/cost",
            OccurredAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
        };
        Assert.True((await scope.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(message)).IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        Assert.True(await outbox.MarkDeadLetteredAsync(message.EventId, "private-broker-marker", message.OccurredAt, 0));
        foreach (var hostMarker in new[] { typeof(PricingHostMarker), typeof(CostingHostMarker), typeof(PlatformHostMarker), typeof(GatewayHostMarker) })
        {
            var start = BusinessProcess.StartInfo(hostMarker.Assembly.Location, "Pricing", "invalid business configuration");
            start.ArgumentList.Add("operation-journal");
            start.ArgumentList.Add("show");
            start.ArgumentList.Add(message.EventId.ToString());
            start.Environment["ConnectionStrings__OperationJournal"] = database.ConnectionString;

            var result = await BusinessProcess.RunToExitAsync(start);

            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal(message.EventId, json.RootElement.GetProperty("messageId").GetGuid());
            Assert.Equal("DeadLettered", json.RootElement.GetProperty("state").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("retryRevision").GetInt64());
            Assert.Equal("operation_journal.delivery_failed", json.RootElement.GetProperty("failureCode").GetString());
            Assert.DoesNotContain("private-payload-marker", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-broker-marker", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Now listening", result.Output, StringComparison.Ordinal);
        }
    }

    [PostgresFact]
    public async Task ShowDelivery_DoesNotMigrateStorage_AndDistinguishesMissingRecordsFromStorageFailure()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        var start = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", "invalid business configuration");
        start.ArgumentList.Add("operation-journal");
        start.ArgumentList.Add("show");
        start.ArgumentList.Add(Guid.NewGuid().ToString());
        start.Environment["ConnectionStrings__OperationJournal"] = database.ConnectionString;

        var unavailable = await BusinessProcess.RunToExitAsync(start);
        Assert.Equal(1, unavailable.ExitCode);
        Assert.Equal("operation_journal.command_failed", unavailable.Output.Trim());

        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var missing = await BusinessProcess.RunToExitAsync(start);
        Assert.Equal(3, missing.ExitCode);
        Assert.Equal("operation_journal.delivery_not_found", missing.Output.Trim());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid connection;Password=<do-not-echo-command-password>")]
    public async Task ShowDelivery_InvalidConfigurationFailsWithoutEchoingItsValue(string? connection)
    {
        var start = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", "invalid business configuration");
        start.ArgumentList.Add("operation-journal");
        start.ArgumentList.Add("show");
        start.ArgumentList.Add(Guid.NewGuid().ToString());
        start.Environment["ConnectionStrings__OperationJournal"] = connection;

        var result = await BusinessProcess.RunToExitAsync(start);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("operation_journal.command_failed", result.Output.Trim());
    }

    [Theory]
    [InlineData(typeof(PricingHostMarker))]
    [InlineData(typeof(CostingHostMarker))]
    [InlineData(typeof(PlatformHostMarker))]
    [InlineData(typeof(GatewayHostMarker))]
    public async Task InvalidMaintenanceArguments_ExitSafelyBeforeStartingTheBusinessHost(Type hostMarker)
    {
        var start = BusinessProcess.StartInfo(hostMarker.Assembly.Location, "Pricing", "invalid business configuration");
        start.Environment["Jwt__SigningKey"] = "invalid";
        foreach (var context in new[] { "Identity", "Platform", "Files", "Auditing", "Scheduling", "Costing" })
        {
            start.Environment[$"ConnectionStrings__{context}"] = "invalid business configuration";
        }
        start.ArgumentList.Add("operation-journal");
        start.ArgumentList.Add("show");
        start.ArgumentList.Add("not-a-message-id");
        start.Environment["ConnectionStrings__OperationJournal"] = "invalid connection;Password=<do-not-echo-command-password>";

        var result = await BusinessProcess.RunToExitAsync(start);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("operation_journal.command_invalid", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-echo-command-password", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", result.Output, StringComparison.Ordinal);
    }
}
