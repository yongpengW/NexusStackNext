using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingConnectionRecoveryTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public Task CostWrite_AfterDatabaseRecovery_AcceptsWithoutReplayingTheCommand() => AssertRecoveryAsync(fee: false);

    [PostgresFact]
    public Task FeeWrite_AfterDatabaseRecovery_AcceptsWithoutReplayingTheCommand() => AssertRecoveryAsync(fee: true);

    [PostgresFact]
    public async Task ConnectionLostDuringWrite_DoesNotReplayTheCommand_AndRollsBackItsTaskAndQuote()
    {
        await using var app = await StartWriterAsync();
        app.Authenticate();
        var item = Guid.NewGuid();
        var original = new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m);
        using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), original);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var change = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, Cost = 96m };
        await database.DisconnectDuringNextTaskInsertAsync(change.RequestId);
        using var failed = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), change);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var missing = await app.Client.GetAsync(new Uri($"/api/pricing/tasks/{change.RequestId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var response = await app.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/items/{item}", UriKind.Relative));
        var quote = response.GetProperty("data").Deserialize<PriceQuoteView>(JsonSerializerOptions.Web)!;
        Assert.Equal(1, quote.Version);
        Assert.Equal(80m, quote.Cost);
        // A new, explicit caller attempt can succeed. The server must not have done it silently.
        using var retried = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), change);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
    }

    private async Task AssertRecoveryAsync(bool fee)
    {
        await using var app = await StartWriterAsync();
        app.Authenticate();
        var item = Guid.NewGuid();
        var original = new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m);
        using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), original);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        try
        {
            await database.SetAvailableAsync(false);
            using var live = await app.Client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally { await database.SetAvailableAsync(true); }

        var requestId = Guid.NewGuid();
        using var recovered = fee
            ? await app.Client.PostAsJsonAsync(new Uri("/api/pricing/fee", UriKind.Relative), new UpdatePricingFee(requestId, item, 1, 0.3m))
            : await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), original with { RequestId = requestId, ExpectedVersion = 1, Cost = 96m });
        Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
        var response = await app.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/items/{item}", UriKind.Relative));
        var quote = response.GetProperty("data").Deserialize<PriceQuoteView>(JsonSerializerOptions.Web)!;
        Assert.Equal(2, quote.Version);
        Assert.Equal(fee ? 80m : 96m, quote.Cost);
        Assert.Equal(fee ? 0.3m : 0.2m, quote.FeeRate);
        using var task = await app.Client.GetAsync(new Uri($"/api/pricing/tasks/{requestId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, task.StatusCode);
    }

    private Task<BusinessProcess> StartWriterAsync()
    {
        // A single idle physical connection makes the broken-pool boundary deterministic.
        // Maintenance must not consume that connection before the next HTTP command.
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        var settings = new Dictionary<string, string>
        {
            ["Pricing__AuditDelivery__Cleanup__Enabled"] = "false",
            ["Pricing__AuditDelivery__PolicyMaintenance__Enabled"] = "false",
            ["Pricing__AuditDelivery__RecoveryMaintenance__Enabled"] = "false",
        };
        return BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", connection, settings: settings);
    }
}
