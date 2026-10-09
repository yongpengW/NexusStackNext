using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingExportJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Ordinary_owner_exports_frozen_csv_through_gateway_and_downloads_with_new_session_after_logout()
    {
        await using var platformDatabase = await databases.CreateAsync();
        await using var pricingDatabase = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        var settings = new Dictionary<string, string>(certificates.Settings)
        {
            ["Jwt__SigningKey"] = BusinessProcess.SigningKey,
            ["Kestrel__Endpoints__Public__Url"] = "http://127.0.0.1:0",
            ["Kestrel__Endpoints__Producer__Url"] = "https://127.0.0.1:0",
        };
        await using var platform = await PlatformHostProcess.StartAsync(platformDatabase.ConnectionString, "export-root-test-password", settings: settings);
        Assert.NotNull(platform.HttpsAddress);
        var client = certificates.PricingClientOptions(platform.HttpsAddress);
        var pricingSettings = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Testing",
            ["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri,
            ["Pricing__Exports__Enabled"] = "true",
            ["Pricing__Exports__Execution__PollInterval"] = "00:00:00.100",
            ["Pricing__Exports__Files__BaseAddress"] = client.BaseAddress,
            ["Pricing__Exports__Files__ClientCertificatePath"] = client.ClientCertificatePath,
            ["Pricing__Exports__Files__ClientKeyPath"] = client.ClientKeyPath,
            ["Pricing__Exports__Files__RootCertificatePaths__0"] = client.RootCertificatePaths[0],
            ["Pricing__Exports__Files__RevocationMode"] = "NoCheck",
        };
        await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString,
            settings: pricingSettings);
        var routeFile = Path.Combine(Path.GetTempPath(), "nsn-export-routes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var routes = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.business.json")))!;
            foreach (var cluster in routes["clusters"]!.AsArray())
            {
                var address = cluster!["clusterId"]!.GetValue<string>() == "pricing-host" ? pricing.Client.BaseAddress : platform.Client.BaseAddress;
                foreach (var destination in cluster["destinations"]!.AsArray()) { destination!["address"] = address!.AbsoluteUri; }
            }
            await File.WriteAllTextAsync(routeFile, routes.ToJsonString());
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routeFile,
                new Dictionary<string, string> { ["IdentitySession__BaseAddress"] = platform.Client.BaseAddress.AbsoluteUri });
            using var root = new HttpClient { BaseAddress = gateway.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
            using var owner = new HttpClient { BaseAddress = gateway.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
            var user = await CreateAsync(owner, "/api/identity/users", new { userName = "export-owner", password = "export-owner-test-password" });
            await UserLifecycleHttpTests.LoginAsync(root, "journey-root", "export-root-test-password");
            await UserLifecycleHttpTests.LoginAsync(owner, "export-owner", "export-owner-test-password");
            var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Own exports", sortOrder = 1 });
            var menuId = menu.GetProperty("menuId").GetString();
            foreach (var permission in new[]
            {
                ("/api/pricing/exports", "POST"), ("/api/pricing/exports", "GET"), ("/api/pricing/exports/{exportId}", "GET"),
                ("/api/pricing/exports/{exportId}/artifact", "GET"), ("/api/files/{fileId}", "GET"),
            }) { _ = await CreateAsync(root, "/api/identity/api-resources", new { path = permission.Item1, method = permission.Item2, menuId }); }
            var role = await CreateAsync(root, "/api/identity/roles", new { code = "export-owner", name = "Own exports" });
            var roleId = role.GetProperty("roleId").GetString();
            using (var granted = await root.PostAsync(Relative($"/api/identity/roles/{roleId}/menus/{menuId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode); }
            using (var assigned = await root.PostAsync(Relative($"/api/identity/users/{user.GetProperty("userId").GetString()}/roles/{roleId}"), null))
            { Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode); }
            var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
            using (var cost = await root.PostAsJsonAsync(Relative("/api/pricing/cost"), new { requestId = Guid.NewGuid(), itemId = item, expectedVersion = "0", cost = 80m, feeRate = 0.2m }))
            { Assert.Equal(HttpStatusCode.Accepted, cost.StatusCode); }
            foreach (var malformed in new[]
            {
                "{\"requestId\":\"" + Guid.NewGuid() + "\",\"itemIds\":[],\"ownerId\":\"another-owner\"}",
                "{\"requestId\":\"" + Guid.NewGuid() + "\",\"requestId\":\"" + Guid.NewGuid() + "\",\"itemIds\":[]}",
                "{\"requestId\":\"" + Guid.NewGuid() + "\",\"itemIds\":[],\"formatVersion\":2}",
                "{\"requestId\":\"" + Guid.NewGuid() + "\",\"itemIds\":[],\"calculationState\":\"unknown\"}",
            })
            {
                using var body = new StringContent(malformed, Encoding.UTF8, "application/json");
                using var rejected = await owner.PostAsync(Relative("/api/pricing/exports"), body);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            using (var content = new StringContent("{}", Encoding.UTF8, "text/plain"))
            using (var rejected = await owner.PostAsync(Relative("/api/pricing/exports"), content))
            { Assert.Equal(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode); }
            using (var content = new UnknownLengthJsonContent(new byte[262_145]))
            using (var rejected = await owner.PostAsync(Relative("/api/pricing/exports"), content))
            { Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode); }
            using (var rejected = await owner.PostAsJsonAsync(Relative("/api/pricing/exports"), new { requestId = Guid.NewGuid(), itemIds = new[] { Guid.NewGuid() } }))
            { Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); }
            using (var list = await owner.GetAsync(Relative("/api/pricing/exports")))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                Assert.Empty((await list.Content.ReadApiDataAsync()).GetProperty("items").EnumerateArray());
            }
            var request = new { requestId = Guid.NewGuid(), itemIds = new[] { item }, calculationState = "Any", formatVersion = 1, columnSetVersion = 1 };
            using var accepted = await owner.PostAsJsonAsync(Relative("/api/pricing/exports"), request);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var original = await accepted.Content.ReadApiDataAsync();
            var exportId = original.GetProperty("exportId").GetGuid();
            using (var costChanged = await root.PostAsJsonAsync(Relative("/api/pricing/cost"), new { requestId = Guid.NewGuid(), itemId = item, expectedVersion = "1", cost = 120m, feeRate = 0.2m }))
            { Assert.Equal(HttpStatusCode.Accepted, costChanged.StatusCode); }
            using (var foreign = await root.GetAsync(Relative($"/api/pricing/exports/{exportId}"))) { Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); }
            var oldToken = owner.DefaultRequestHeaders.Authorization;
            using (var loggedOut = await owner.PostAsync(Relative("/api/identity/logout"), null)) { Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode); }
            using (var invalid = await owner.GetAsync(Relative($"/api/pricing/exports/{exportId}"))) { Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode); }
            await UserLifecycleHttpTests.LoginAsync(owner, "export-owner", "export-owner-test-password");
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            JsonElement completed;
            while (true)
            {
                using var status = await owner.GetAsync(Relative($"/api/pricing/exports/{exportId}"), wait.Token);
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                completed = await status.Content.ReadApiDataAsync();
                if (completed.GetProperty("state").GetString() == "Succeeded") { break; }
                Assert.Contains(completed.GetProperty("state").GetString(), new[] { "Queued", "Generating", "Publishing" });
                await Task.Delay(100, wait.Token);
            }
            Assert.Equal(original.GetProperty("snapshotDigest").GetString(), completed.GetProperty("snapshotDigest").GetString());
            using var artifactResponse = await owner.GetAsync(Relative($"/api/pricing/exports/{exportId}/artifact"));
            Assert.Equal(HttpStatusCode.OK, artifactResponse.StatusCode);
            var artifact = await artifactResponse.Content.ReadApiDataAsync();
            Assert.Equal("Available", artifact.GetProperty("state").GetString());
            var downloadPath = artifact.GetProperty("downloadPath").GetString()!;
            using var download = await owner.GetAsync(Relative(downloadPath));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            const string expected = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
                + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
            Assert.Equal(Encoding.UTF8.GetBytes(expected), await download.Content.ReadAsByteArrayAsync());
            using (var rootCannotOwn = await root.GetAsync(Relative(downloadPath))) { Assert.Equal(HttpStatusCode.NotFound, rootCannotOwn.StatusCode); }
            owner.DefaultRequestHeaders.Authorization = oldToken;
            using (var revoked = await owner.GetAsync(Relative(downloadPath))) { Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode); }
        }
        finally { File.Delete(routeFile); }
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    [PostgresFact]
    public async Task Pricing_process_restart_publishes_the_original_selected_intent_without_refreezing_changed_quotes()
    {
        await using var platformDatabase = await databases.CreateAsync();
        await using var pricingDatabase = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        var platformSettings = new Dictionary<string, string>(certificates.Settings)
        {
            ["Jwt__SigningKey"] = BusinessProcess.SigningKey,
            ["Kestrel__Endpoints__Public__Url"] = "http://127.0.0.1:0",
            ["Kestrel__Endpoints__Producer__Url"] = "https://127.0.0.1:0",
        };
        await using var platform = await PlatformHostProcess.StartAsync(platformDatabase.ConnectionString, "export-root-test-password", settings: platformSettings);
        Assert.NotNull(platform.HttpsAddress);
        await UserLifecycleHttpTests.LoginAsync(platform.Client, "journey-root", "export-root-test-password");
        var client = certificates.PricingClientOptions(platform.HttpsAddress);
        var settings = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Testing",
            ["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri,
            ["Pricing__Exports__Enabled"] = "true",
            ["Pricing__Exports__Worker__Enabled"] = "false",
            ["Pricing__Exports__Files__BaseAddress"] = client.BaseAddress,
            ["Pricing__Exports__Files__ClientCertificatePath"] = client.ClientCertificatePath,
            ["Pricing__Exports__Files__ClientKeyPath"] = client.ClientKeyPath,
            ["Pricing__Exports__Files__RootCertificatePaths__0"] = client.RootCertificatePaths[0],
            ["Pricing__Exports__Files__RevocationMode"] = "NoCheck",
        };
        await using var first = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, settings: settings);
        first.Client.DefaultRequestHeaders.Authorization = platform.Client.DefaultRequestHeaders.Authorization;
        var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
        using (var cost = await first.Client.PostAsJsonAsync(Relative("/api/pricing/cost"), new { requestId = Guid.NewGuid(), itemId = item, expectedVersion = "0", cost = 80m, feeRate = 0.2m }))
        { Assert.Equal(HttpStatusCode.Accepted, cost.StatusCode); }
        using var accepted = await first.Client.PostAsJsonAsync(Relative("/api/pricing/exports"), new { requestId = Guid.NewGuid(), itemIds = new[] { item } });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var original = await accepted.Content.ReadApiDataAsync();
        var id = original.GetProperty("exportId").GetGuid();
        var services = new ServiceCollection().AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        services.AddPricingPostgres(pricingDatabase.ConnectionString).AddPricingExportPersistence();
        services.AddPricingExportFiles(client, "Testing").AddPricingExportProcessing();
        PricingExportStatus selected;
        await using (var producer = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        await using (var scope = producer.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var lease = (await sender.SendAsync(new ClaimPricingExport())).Value!;
            var generated = await sender.SendAsync(new GeneratePricingExport(lease));
            Assert.True(generated.IsSuccess, generated.Error?.Code);
            selected = generated.Value;
            Assert.Equal("Publishing", selected.State);
        }
        using (var cost = await first.Client.PostAsJsonAsync(Relative("/api/pricing/cost"), new { requestId = Guid.NewGuid(), itemId = item, expectedVersion = "1", cost = 120m, feeRate = 0.2m }))
        { Assert.Equal(HttpStatusCode.Accepted, cost.StatusCode); }
        var address = first.Client.BaseAddress!;
        await first.CrashAsync();
        settings["Pricing__Exports__Worker__Enabled"] = "true";
        settings["Pricing__Exports__Execution__PollInterval"] = "00:00:00.100";
        settings["Kestrel__Endpoints__Public__Url"] = address.AbsoluteUri;
        await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, settings: settings);
        restarted.Client.DefaultRequestHeaders.Authorization = platform.Client.DefaultRequestHeaders.Authorization;
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        JsonElement completed;
        while (true)
        {
            using var response = await restarted.Client.GetAsync(Relative($"/api/pricing/exports/{id}"), wait.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            completed = await response.Content.ReadApiDataAsync();
            if (completed.GetProperty("state").GetString() == "Succeeded") { break; }
            Assert.Equal("Publishing", completed.GetProperty("state").GetString());
            await Task.Delay(100, wait.Token);
        }
        Assert.Equal(selected.PublicationId, completed.GetProperty("publicationId").GetGuid());
        Assert.Equal(selected.FileId, long.Parse(completed.GetProperty("fileId").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(original.GetProperty("snapshotDigest").GetString(), completed.GetProperty("snapshotDigest").GetString());
        using var download = await platform.Client.GetAsync(Relative($"/api/files/{selected.FileId}"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        const string expected = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await download.Content.ReadAsByteArrayAsync());
    }

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _bytes;
        public UnknownLengthJsonContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
    }
    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T body)
    {
        using var response = await client.PostAsJsonAsync(Relative(path), body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
