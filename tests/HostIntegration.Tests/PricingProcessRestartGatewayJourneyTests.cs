using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingProcessRestartGatewayJourneyTests
{
    [PostgresFact]
    public async Task Pricing_process_restart_preserves_committed_quote_through_gateway()
    {
        await using var identity = await IdentityJourneyDatabase.CreateAsync();
        await using var pricingDatabase = await IdentityJourneyDatabase.CreateAsync();
        await identity.MigrateThroughCliAsync();
        await MigratePricingAsync(pricingDatabase.ConnectionString);

        var settings = new Dictionary<string, string>
        {
            ["Jwt__SigningKey"] = BusinessProcess.SigningKey,
            ["Pricing__Messaging__Enabled"] = "false",
            ["Pricing__Webhook__Enabled"] = "false",
            ["Pricing__Worker__Enabled"] = "false",
        };
        await using var platform = await PlatformHostProcess.StartAsync(identity.ConnectionString, "pricing-restart-root-password", settings: settings);
        settings["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri;
        var routePath = Path.Combine(Path.GetTempPath(), "nsn-pricing-restart-routes-" + Guid.NewGuid().ToString("N") + ".json");
        BusinessProcess? pricing = null;
        BusinessProcess? restarted = null;
        BusinessProcess? gateway = null;
        try
        {
            pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, settings: settings);
            await WriteRoutesAsync(routePath, platform.Client.BaseAddress!, pricing.Client.BaseAddress!);
            gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath, settings);
            using var root = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
            await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "pricing-restart-root-password");
            var itemId = Guid.NewGuid();
            using (var accepted = await root.PostAsJsonAsync(Relative("/api/pricing/cost"), new
            {
                requestId = Guid.NewGuid(),
                itemId,
                expectedVersion = "0",
                cost = 80m,
                feeRate = 0.2m,
            }))
            { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }

            var beforeRestart = await ReadQuoteAsync(root, itemId);
            Assert.Equal(80m, beforeRestart.GetProperty("cost").GetDecimal());
            Assert.Equal(0.2m, beforeRestart.GetProperty("feeRate").GetDecimal());

            await pricing.CrashAsync();
            await gateway.DisposeAsync();
            gateway = null;
            restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, settings: settings);
            await WriteRoutesAsync(routePath, platform.Client.BaseAddress!, restarted.Client.BaseAddress!);
            gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath, settings);
            using var recovered = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
            await PlatformSettingsAccessTests.LoginAsync(recovered, "journey-root", "pricing-restart-root-password");
            var afterRestart = await ReadQuoteAsync(recovered, itemId);
            Assert.Equal(beforeRestart.GetProperty("version").GetString(), afterRestart.GetProperty("version").GetString());
            Assert.Equal(beforeRestart.GetProperty("cost").GetDecimal(), afterRestart.GetProperty("cost").GetDecimal());
            Assert.Equal(beforeRestart.GetProperty("feeRate").GetDecimal(), afterRestart.GetProperty("feeRate").GetDecimal());
        }
        finally
        {
            if (gateway is not null) { await gateway.DisposeAsync(); }
            if (restarted is not null) { await restarted.DisposeAsync(); }
            if (pricing is not null) { await pricing.DisposeAsync(); }
            File.Delete(routePath);
        }
    }

    private static async Task<JsonElement> ReadQuoteAsync(HttpClient client, Guid itemId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var response = await client.GetAsync(Relative($"/api/pricing/items/{itemId}"), timeout.Token);
            if (response.IsSuccessStatusCode) { return await response.Content.ReadApiDataAsync(); }
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable,
                $"Unexpected HTTP {(int)response.StatusCode} while reading the pricing quote.");
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task MigratePricingAsync(string connection)
    {
        await using var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true);
        var start = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", connection);
        start.ArgumentList.Add("migrate-pricing");
        Assert.Equal(0, (await BusinessProcess.RunToExitAsync(start)).ExitCode);
    }

    private static async Task WriteRoutesAsync(string path, Uri platform, Uri pricing)
    {
        var table = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.business.json")))!;
        foreach (var cluster in table["clusters"]!.AsArray())
        {
            var address = cluster!["clusterId"]!.GetValue<string>() == "pricing-host" ? pricing : platform;
            foreach (var destination in cluster["destinations"]!.AsArray()) { destination!["address"] = address.AbsoluteUri; }
        }
        await File.WriteAllTextAsync(path, table.ToJsonString());
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
}
