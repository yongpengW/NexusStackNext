using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class BusinessFactCapacityAdmissionTests
{
    [PostgresFact]
    public async Task PricingLedgerContention_DoesNotAcknowledgeCostOrCommitFee_AndOriginalRequestsRecoverOnce()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(database.ConnectionString);
        var connection = new CommittedFactCapacityWriteOptions { Timeout = TimeSpan.FromMilliseconds(250) }.ConfigureConnection(database.ConnectionString);
        await using var app = TaskOperationTests.CreatePricingApp(connection, null);
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var cost = new CostCalculatedV1
        {
            EventId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            CostRevision = 1,
            UnitCost = 100m,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var message = OutboxEntry.From(cost, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope();
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "pricing"))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.False(await processor.HandleAsync(message, deadline.Token));
            Assert.True((await sender.QueryAsync(new GetPriceQuote(cost.ItemId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(cost.EventId))).IsFailure);
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
            await capacityLock.ReleaseAsync();
        }
        Assert.True(await processor.HandleAsync(message));
        var before = (await sender.QueryAsync(new GetPriceQuote(cost.ItemId))).Value;
        Assert.Equal(100m, before.Cost);
        Assert.Equal(1, before.CostingRevision);
        Assert.True(await processor.HandleAsync(message));
        Assert.Equal(before, (await sender.QueryAsync(new GetPriceQuote(cost.ItemId))).Value);
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
        var fee = new UpdatePricingFee(Guid.NewGuid(), cost.ItemId, before.Version, 0.2m);
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "pricing"))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.Equal(CommittedFactCapacityErrors.Busy, (await sender.SendAsync(fee, deadline.Token)).Error);
            Assert.Equal(before, (await sender.QueryAsync(new GetPriceQuote(cost.ItemId))).Value);
            Assert.True((await sender.QueryAsync(new GetRecalculation(fee.RequestId))).IsFailure);
            Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
            await capacityLock.ReleaseAsync();
        }
        Assert.True((await sender.SendAsync(fee)).IsSuccess);
        var current = (await sender.QueryAsync(new GetPriceQuote(cost.ItemId))).Value;
        Assert.Equal(before.Version + 1, current.Version);
        Assert.Equal(0.2m, current.FeeRate);
        Assert.Equal(100m, current.Cost);
        Assert.True((await sender.SendAsync(fee)).IsSuccess);
        Assert.Equal(current, (await sender.QueryAsync(new GetPriceQuote(cost.ItemId))).Value);
        Assert.Equal(3, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task PricingLedgerContention_RollsBackManualInputAndCompletion_AndSameLeaseCanRecover()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(database.ConnectionString);
        var settings = new Dictionary<string, string> { ["Pricing__AuditDelivery__CapacityWrite__Timeout"] = "00:00:00.250" };
        await using var host = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
        host.Authenticate();
        using var client = new HttpClient { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(3) };
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = "0", cost = 80m, feeRate = 0.2m };
        var path = new Uri("/api/pricing/cost", UriKind.Relative);
        var connection = new CommittedFactCapacityWriteOptions { Timeout = TimeSpan.FromMilliseconds(250) }.ConfigureConnection(database.ConnectionString);
        await using var app = TaskOperationTests.CreatePricingApp(connection, null);
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "pricing"))
        {
            using var rejected = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("audit_capacity.busy", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            Assert.True((await sender.QueryAsync(new GetPriceQuote(request.itemId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(request.requestId))).IsFailure);
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
            await capacityLock.ReleaseAsync();
        }
        using var accepted = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var before = (await sender.QueryAsync(new GetPriceQuote(request.itemId))).Value;
        Assert.Equal(80m, before.Cost);
        Assert.Equal(0.2m, before.FeeRate);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "pricing"))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew();
            var rejected = await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch), deadline.Token);
            Assert.Equal(CommittedFactCapacityErrors.Busy, rejected.Error);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(before, (await sender.QueryAsync(new GetPriceQuote(request.itemId))).Value);
            Assert.Equal("Running", (await sender.QueryAsync(new GetRecalculation(request.requestId))).Value.State);
            Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
            await capacityLock.ReleaseAsync();
        }
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal("Succeeded", (await sender.QueryAsync(new GetRecalculation(request.requestId))).Value.State);
        Assert.Equal(100m, (await sender.QueryAsync(new GetPriceQuote(request.itemId))).Value.BreakEvenPrice);
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task CostingLedgerContention_RollsBackManualInputAndCompletion_AndSameLeaseCanRecover()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        var settings = new Dictionary<string, string> { ["Costing__AuditDelivery__CapacityWrite__Timeout"] = "00:00:00.250" };
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: settings);
        host.Authenticate();
        using var client = new HttpClient { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(3) };
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = "0", purchaseCost = 80m, freightCost = 20m };
        var path = new Uri("/api/costing/cost", UriKind.Relative);
        var connection = new CommittedFactCapacityWriteOptions { Timeout = TimeSpan.FromMilliseconds(250) }.ConfigureConnection(database.ConnectionString);
        await using var app = TaskOperationTests.CreateCostingApp(connection, null);
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "costing"))
        {
            using var rejected = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("audit_capacity.busy", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            Assert.True((await sender.QueryAsync(new GetCostSheet(request.itemId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetCostCalculation(request.requestId))).IsFailure);
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
            await capacityLock.ReleaseAsync();
        }
        using var accepted = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var before = (await sender.QueryAsync(new GetCostSheet(request.itemId))).Value;
        var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "costing"))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew();
            var rejected = await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch), deadline.Token);
            Assert.Equal(CommittedFactCapacityErrors.Busy, rejected.Error);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(before, (await sender.QueryAsync(new GetCostSheet(request.itemId))).Value);
            Assert.Equal("Running", (await sender.QueryAsync(new GetCostCalculation(request.requestId))).Value.State);
            Assert.True((await sender.QueryAsync(new GetCostDelivery(request.requestId))).IsFailure);
            Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
            await capacityLock.ReleaseAsync();
        }
        Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal("Succeeded", (await sender.QueryAsync(new GetCostCalculation(request.requestId))).Value.State);
        Assert.Equal(100m, (await sender.QueryAsync(new GetCostSheet(request.itemId))).Value.UnitCost);
        var entries = await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        Assert.Equal(2, entries.Count(entry => entry.EventName == CostSheetCommittedV1.Name));
        Assert.Equal(request.requestId, Assert.Single(entries, entry => entry.EventName == CostCalculatedV1.Name).Id);
    }
}
