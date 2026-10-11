using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.PricingHost;
using StackExchange.Redis;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class CostPricingGatewayJourneyTests
{
    [CostPricingRedisBrokerFact]
    public async Task EmptyDatabases_GatewayAuthorizedBatch_RecoversToPricingRedisWebhookAndAudit()
    {
        await using var identity = await IdentityJourneyDatabase.CreateAsync();
        await using var costs = await IdentityJourneyDatabase.CreateAsync();
        await using var prices = await IdentityJourneyDatabase.CreateAsync();
        await identity.MigrateThroughCliAsync();
        await TaskOperationJourneyTests.MigrateJournalAsync(typeof(GatewayHostMarker).Assembly.Location, identity.ConnectionString);
        await MigrateBusinessAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString);
        await MigrateBusinessAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", prices.ConnectionString);

        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-chain", ClientName = prefix };
        await using var rabbitProxy = new RabbitMqFaultProxy(broker);
        var costSubscription = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var tap = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-tap" };
        var facts = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-facts" };
        var operations = new EventSubscription { EventName = OperationObservedV1.Name, ConsumerName = prefix + "-operations" };
        var webhook = new EventSubscription { EventName = PricingRecalculationCompletedV1.Name, ConsumerName = "pricing-webhook" };
        var topology = EventTopology.Create(broker.ExchangeName, [costSubscription, tap, facts, operations, webhook]);
        var redis = Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")!;
        await using var cacheProxy = new RedisFaultProxy(redis);
        var settings = AuditBusinessJourneyTests.Settings(rabbitProxy.Options, facts.ConsumerName);
        settings["Auditing__Messaging__OperationConsumerName"] = operations.ConsumerName;
        settings["Jwt__SigningKey"] = BusinessProcess.SigningKey;
        PlatformHostProcess? platform = null;
        platform = await PlatformHostProcess.StartAsync(identity.ConnectionString, "chain-root-password", settings: settings);
        settings["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri;
        settings["Costing__Scheduling__Enabled"] = "false";
        settings["Costing__Messaging__Enabled"] = "true";
        settings["Pricing__Messaging__Enabled"] = "true";
        settings["Pricing__Messaging__ConsumerName"] = costSubscription.ConsumerName;
        settings["Pricing__Tasks__PollInterval"] = "00:00:00.100";
        settings["OperationJournal__Storage__Provider"] = "Postgres";
        foreach (var context in new[] { "Costing", "Pricing" })
        {
            settings[context + "__Cache__Enabled"] = "true";
            settings[context + "__Cache__ConnectionString"] = cacheProxy.ConnectionString;
            settings[context + "__Cache__Namespace"] = prefix + "-" + context.ToLowerInvariant();
        }
        var costSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
        {
            ["ConnectionStrings__OperationJournal"] = costs.ConnectionString,
        };
        var priceSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
        {
            ["ConnectionStrings__Pricing"] = prices.ConnectionString,
            ["ConnectionStrings__OperationJournal"] = prices.ConnectionString,
            ["Pricing__Webhook__Enabled"] = "true",
            ["Pricing__Webhook__MaxAttempts"] = "1",
        };
        var routes = Path.Combine(Path.GetTempPath(), "nsn-chain-routes-" + Guid.NewGuid().ToString("N") + ".json");
        using var certificates = new GeneratedFileCertificates();
        await using var receiver = await CompletionReceiver.StartAsync(certificates);
        priceSettings["Pricing__Webhook__Endpoint"] = new Uri(receiver.Address, "/webhooks/pricing").AbsoluteUri;
        var gatewaySettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
        {
            ["OperationJournal__Storage__Provider"] = "Postgres",
            ["ConnectionStrings__OperationJournal"] = identity.ConnectionString,
            ["Gateway__RateLimit__PermitLimit"] = "1000",
        };
        var batchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var traceId = Guid.NewGuid().ToString("N");
        long operatorId = 0;
        var input = new
        {
            batchRequestId = batchId,
            rows = new[]
            {
                new { sourceRow = 10, itemId, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m },
                new { sourceRow = 20, itemId = Guid.NewGuid(), expectedVersion = "1", purchaseCost = 10m, freightCost = 2m },
            },
        };
        JourneyPricingApp? pricing = null;
        BusinessProcess? worker = null;
        BusinessProcess? restoredGateway = null;
        JourneyPricingApp? restartedPricing = null;
        BusinessProcess? finalGateway = null;
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            // The only test replacement is the HTTPS receiver's private CA trust; Pricing still uses real sockets, workers and storage.
            pricing = new JourneyPricingApp(priceSettings, certificates);
            using var pricingAddress = pricing.CreateClient();
            var offlineSettings = new Dictionary<string, string>(costSettings, StringComparer.Ordinal)
            {
                ["RabbitMQ__HostName"] = "127.0.0.1",
                ["RabbitMQ__Port"] = "1",
            };
            cacheProxy.SetOffline(true);
            await using (var producer = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString, settings: offlineSettings))
            {
                await WriteRoutesAsync(routes, platform.Client.BaseAddress!, producer.Client.BaseAddress!, pricingAddress.BaseAddress!);
                await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, gatewaySettings);
                using var root = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
                using var user = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
                await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "chain-root-password");
                var registered = await CreateAsync(user, "/api/identity/users", new { userName = "cost-operator", password = "cost-operator-password" });
                operatorId = registered.GetProperty("userId").ReadHttpInt64();
                await PlatformSettingsAccessTests.LoginAsync(user, "cost-operator", "cost-operator-password");
                await AssertDeniedAsync(gateway.Client, batchId, HttpStatusCode.Unauthorized);
                await AssertDeniedAsync(user, batchId, HttpStatusCode.Forbidden);
                await GrantOperatorAsync(root, operatorId);
                using var request = new HttpRequestMessage(HttpMethod.Post, Relative("/api/costing/batches")) { Content = JsonContent.Create(input) };
                request.Headers.Add("traceparent", $"00-{traceId}-1234567890abcdef-01");
                request.Headers.Add("X-Correlation-Id", "cost-pricing-chain");
                using var accepted = await user.SendAsync(request);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                Assert.Equal("Pending", (await accepted.Content.ReadApiDataAsync()).GetProperty("state").GetString());
                using var logout = await user.PostAsync(Relative("/api/identity/logout"), null);
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
                // Terminate the accepting OS process. The next process has no caller token and must resume persisted work.
                await producer.CrashAsync();
            }

            worker = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString,
                worker: true, settings: costSettings);
            await WriteRoutesAsync(routes, platform.Client.BaseAddress!, worker.Client.BaseAddress!, pricingAddress.BaseAddress!);
            restoredGateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, gatewaySettings);
            using var operatorClient = new HttpClient { BaseAddress = restoredGateway.Client.BaseAddress };
            await PlatformSettingsAccessTests.LoginAsync(operatorClient, "cost-operator", "cost-operator-password");
            await PlatformSettingsAccessTests.LoginAsync(restoredGateway.Client, "journey-root", "chain-root-password");
            var completed = await WaitDataAsync(operatorClient, $"/api/costing/batches/{batchId}", data => data.GetProperty("state").GetString() == "CompletedWithErrors");
            Assert.Equal(2, completed.GetProperty("checkpoint").GetInt32());
            Assert.Equal(1, completed.GetProperty("imported").GetInt32());
            Assert.Equal(1, completed.GetProperty("rejected").GetInt32());
            var rows = await WaitDataAsync(operatorClient, $"/api/costing/batches/{batchId}/rows", _ => true);
            Assert.Equal("costing.version_conflict", rows[1].GetProperty("errorCode").GetString());
            var taskId = rows[0].GetProperty("taskId").GetGuid();
            var task = await WaitDataAsync(operatorClient, $"/api/costing/tasks/{taskId}", data => data.GetProperty("state").GetString() == "Succeeded");
            Assert.Equal(1, task.GetProperty("attempts").GetInt32());
            var attempts = await WaitDataAsync(operatorClient, $"/api/costing/batches/{batchId}/attempts", _ => true);
            Assert.Single(attempts.EnumerateArray());

            // Redis is still unavailable: result queries must fall back without blocking the accepted task.
            var cost = await WaitDataAsync(operatorClient, $"/api/costing/items/{itemId}/display", data => data.GetProperty("unitCost").ValueKind == JsonValueKind.Number);
            Assert.Equal(100m, cost.GetProperty("unitCost").GetDecimal());
            var quote = await WaitDataAsync(operatorClient, $"/api/pricing/items/{itemId}", data => data.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number);
            Assert.Equal(100m, quote.GetProperty("breakEvenPrice").GetDecimal());
            var completion = await receiver.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(itemId, completion.ItemId);
            await WaitDataAsync(operatorClient, $"/api/pricing/webhooks/{completion.EventId}", data => data.GetProperty("state").GetString() == "DeadLettered");

            cacheProxy.SetOffline(false);
            await using var connection = await ConnectionMultiplexer.ConnectAsync(redis);
            var cache = connection.GetDatabase();
            await WaitCacheAsync(operatorClient, cache, $"/api/costing/items/{itemId}/display", $"{settings["Costing__Cache__Namespace"]}:costing:sheet:v1:{itemId:N}", "UnitCost", 100m);
            await WaitCacheAsync(operatorClient, cache, $"/api/pricing/items/{itemId}", $"{settings["Pricing__Cache__Namespace"]}:pricing:quote:v2:{itemId:N}", "BreakEvenPrice", 100m);

            var original = await AuditBusinessJourneyTests.ReadEnvelopeAsync(broker, tap.QueueName, CostCalculatedV1.Name);
            await using var bus = new RabbitMqEventBus(broker);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            // A subsequent distinct event is a public consumer barrier; duplicate handling has finished when its quote is visible.
            var barrier = new CostCalculatedV1 { EventId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, ItemId = Guid.NewGuid(), CostRevision = 1, UnitCost = 5m };
            Assert.True((await bus.PublishAsync(new EventEnvelope
            {
                MessageId = barrier.EventId,
                EventName = barrier.EventName,
                OccurredAt = barrier.OccurredAt,
                Payload = new SystemTextJsonIntegrationEventSerializer().Serialize(barrier),
            })).IsSuccess);
            await WaitDataAsync(restoredGateway.Client, $"/api/pricing/items/{barrier.ItemId}", data => data.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number);
            var afterDuplicate = await WaitDataAsync(operatorClient, $"/api/pricing/items/{itemId}", _ => true);
            Assert.Equal(quote.GetProperty("version").GetString(), afterDuplicate.GetProperty("version").GetString());
            using var replay = await operatorClient.PostAsJsonAsync(Relative("/api/costing/batches"), input);
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            Assert.Equal(completed.GetProperty("epoch").GetString(), (await replay.Content.ReadApiDataAsync()).GetProperty("epoch").GetString());

            await pricing.DisposeAsync();
            pricing = null;
            restartedPricing = new JourneyPricingApp(priceSettings, certificates);
            using var restartedAddress = restartedPricing.CreateClient();
            await WriteRoutesAsync(routes, platform.Client.BaseAddress!, worker.Client.BaseAddress!, restartedAddress.BaseAddress!);
            finalGateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, gatewaySettings);
            await PlatformSettingsAccessTests.LoginAsync(finalGateway.Client, "journey-root", "chain-root-password");
            var retained = await WaitDataAsync(finalGateway.Client, $"/api/pricing/webhooks/{completion.EventId}", _ => true);
            Assert.Equal("DeadLettered", retained.GetProperty("state").GetString());
            var attemptsBeforeRetry = retained.GetProperty("attemptCount").GetInt32();
            using var anonymousClient = new HttpClient { BaseAddress = finalGateway.Client.BaseAddress };
            using (var anonymousRetry = await anonymousClient.PostAsJsonAsync(Relative($"/api/pricing/webhooks/{completion.EventId}/retry"), new { expectedAttemptCount = attemptsBeforeRetry }))
            { Assert.Equal(HttpStatusCode.Unauthorized, anonymousRetry.StatusCode); }
            using var deniedOperator = new HttpClient { BaseAddress = finalGateway.Client.BaseAddress };
            await PlatformSettingsAccessTests.LoginAsync(deniedOperator, "cost-operator", "cost-operator-password");
            using (var deniedRetry = await deniedOperator.PostAsJsonAsync(Relative($"/api/pricing/webhooks/{completion.EventId}/retry"), new { expectedAttemptCount = attemptsBeforeRetry }))
            { Assert.Equal(HttpStatusCode.Forbidden, deniedRetry.StatusCode); }
            using var retried = await finalGateway.Client.PostAsJsonAsync(Relative($"/api/pricing/webhooks/{completion.EventId}/retry"), new { expectedAttemptCount = attemptsBeforeRetry });
            Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
            var delivered = await WaitDataAsync(finalGateway.Client, $"/api/pricing/webhooks/{completion.EventId}", data => data.GetProperty("state").GetString() == "Succeeded");
            Assert.Equal(2, delivered.GetProperty("attemptCount").GetInt32());
            Assert.Equal(2, receiver.Requests[completion.EventId]);
            Assert.Equal(1, receiver.EffectCount(completion.EventId));
            Assert.False(receiver.SawBearer);
            var finalQuote = await WaitDataAsync(finalGateway.Client, $"/api/pricing/items/{itemId}", _ => true);
            Assert.Equal(quote.GetProperty("version").GetString(), finalQuote.GetProperty("version").GetString());
            var audit = await WaitDataAsync(finalGateway.Client, $"/api/auditing/entries?source=pricing&subjectId={itemId:D}", data => data.GetArrayLength() >= 3);
            Assert.Contains(audit.EnumerateArray(), entry => entry.GetProperty("fact").GetProperty("action").GetString() == "pricing.price-quote.result-applied");
            var operationsObserved = await WaitDataAsync(finalGateway.Client, "/api/auditing/operations?correlationId=cost-pricing-chain", data => data.GetArrayLength() >= 2);
            var gatewayOperation = Assert.Single(operationsObserved.EnumerateArray(), operation => operation.GetProperty("source").GetString() == "gateway");
            Assert.Contains(operationsObserved.EnumerateArray(), operation => operation.GetProperty("source").GetString() == "costing");
            Assert.Equal(operatorId.ToString(System.Globalization.CultureInfo.InvariantCulture), gatewayOperation.GetProperty("actorId").GetString());

            // Both caches contain 100. A committed change while Redis is offline must invalidate that old value after recovery.
            cacheProxy.SetOffline(true);
            await rabbitProxy.WaitForConnectionAsync(TimeSpan.FromSeconds(15));
            rabbitProxy.SetOffline(true);
            await rabbitProxy.WaitForDisconnectedAsync(TimeSpan.FromSeconds(15));
            var changedBatchId = Guid.NewGuid();
            using var changedBatch = await operatorClient.PostAsJsonAsync(Relative("/api/costing/batches"), new
            {
                batchRequestId = changedBatchId,
                rows = new[] { new { sourceRow = 1, itemId, expectedVersion = cost.GetProperty("version").GetString(), purchaseCost = 105m, freightCost = 20m } },
            });
            Assert.Equal(HttpStatusCode.Accepted, changedBatch.StatusCode);
            await WaitDataAsync(operatorClient, $"/api/costing/batches/{changedBatchId}", data => data.GetProperty("state").GetString() == "Completed");
            await WaitDataAsync(operatorClient, $"/api/costing/items/{itemId}/display", data => data.GetProperty("unitCost").ValueKind == JsonValueKind.Number && data.GetProperty("unitCost").GetDecimal() == 125m);
            var duringDisconnect = await WaitDataAsync(finalGateway.Client, $"/api/pricing/items/{itemId}", _ => true);
            Assert.Equal(100m, duringDisconnect.GetProperty("breakEvenPrice").GetDecimal());
            rabbitProxy.SetOffline(false);
            cacheProxy.SetOffline(false);
            await WaitDataAsync(finalGateway.Client, $"/api/pricing/items/{itemId}", data => data.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number && data.GetProperty("breakEvenPrice").GetDecimal() == 125m);
            await WaitCacheAsync(operatorClient, cache, $"/api/costing/items/{itemId}/display", $"{settings["Costing__Cache__Namespace"]}:costing:sheet:v1:{itemId:N}", "UnitCost", 125m);
            await WaitCacheAsync(finalGateway.Client, cache, $"/api/pricing/items/{itemId}", $"{settings["Pricing__Cache__Namespace"]}:pricing:quote:v2:{itemId:N}", "BreakEvenPrice", 125m);
        }
        finally
        {
            if (finalGateway is not null) { await finalGateway.DisposeAsync(); }
            if (restartedPricing is not null) { await restartedPricing.DisposeAsync(); }
            if (restoredGateway is not null) { await restoredGateway.DisposeAsync(); }
            if (worker is not null) { await worker.DisposeAsync(); }
            if (pricing is not null) { await pricing.DisposeAsync(); }
            if (platform is not null) { await platform.DisposeAsync(); }
            File.Delete(routes);
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
        }
    }

    private static async Task MigrateBusinessAsync(string assembly, string context, string connection)
    {
        await using (var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true))
        {
            var start = BusinessProcess.StartInfo(assembly, context, connection);
            start.ArgumentList.Add("migrate-" + context.ToLowerInvariant());
            Assert.Equal(0, (await BusinessProcess.RunToExitAsync(start)).ExitCode);
        }
        await TaskOperationJourneyTests.MigrateJournalAsync(assembly, connection);
    }

    private static async Task AssertDeniedAsync(HttpClient client, Guid batchId, HttpStatusCode expected)
    {
        foreach (var path in new[] { $"/api/costing/batches/{batchId}", $"/api/costing/batches/{batchId}/rows", $"/api/costing/batches/{batchId}/attempts" })
        {
            using var denied = await client.GetAsync(Relative(path));
            Assert.True(denied.StatusCode == expected, $"{path} returned {(int)denied.StatusCode}, expected {(int)expected}.");
        }
        foreach (var path in new[] { "/api/costing/batches", $"/api/costing/batches/{batchId}/retry", $"/api/costing/batches/{batchId}/cancel" })
        {
            using var denied = await client.PostAsJsonAsync(Relative(path), new { expectedEpoch = "0" });
            Assert.True(denied.StatusCode == expected, $"{path} returned {(int)denied.StatusCode}, expected {(int)expected}.");
        }
    }

    private static async Task GrantOperatorAsync(HttpClient root, long userId)
    {
        var menuId = (await CreateAsync(root, "/api/identity/menus", new { title = "成本操作员", sortOrder = 1 })).GetProperty("menuId").ReadHttpInt64();
        foreach (var (path, method) in new[]
        {
            ("/api/costing/batches", "POST"), ("/api/costing/batches/{batchId}", "GET"), ("/api/costing/batches/{batchId}/rows", "GET"),
            ("/api/costing/batches/{batchId}/attempts", "GET"), ("/api/costing/tasks/{taskId}", "GET"), ("/api/costing/items/{itemId}/display", "GET"),
            ("/api/pricing/items/{itemId}", "GET"), ("/api/pricing/webhooks/{eventId}", "GET"),
        }) { _ = await CreateAsync(root, "/api/identity/api-resources", new { path, method, menuId }); }
        var roleId = (await CreateAsync(root, "/api/identity/roles", new { code = "cost-operator", name = "成本操作员" })).GetProperty("roleId").ReadHttpInt64();
        using var grant = await root.PostAsync(Relative($"/api/identity/roles/{roleId}/menus/{menuId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        using var assign = await root.PostAsync(Relative($"/api/identity/users/{userId}/roles/{roleId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(Relative(path), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static async Task WriteRoutesAsync(string path, Uri platform, Uri costing, Uri pricing)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NexusStackNext.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var table = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src/Gateway/NexusStackNext.Gateway/routes.business.json")))!;
        foreach (var cluster in table["clusters"]!.AsArray())
        {
            var address = cluster!["clusterId"]!.GetValue<string>() switch { "costing-host" => costing, "pricing-host" => pricing, _ => platform };
            foreach (var destination in cluster["destinations"]!.AsArray()) { destination!["address"] = address.AbsoluteUri; }
        }
        await File.WriteAllTextAsync(path, table.ToJsonString());
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> WaitDataAsync(HttpClient client, string path, Func<JsonElement, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (true)
        {
            using var response = await client.GetAsync(Relative(path), timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadApiDataAsync();
                if (predicate(payload)) { return payload; }
            }
            else { Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable, $"Unexpected HTTP {(int)response.StatusCode} at {path}."); }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task WaitCacheAsync(HttpClient client, IDatabase cache, string path, RedisKey key, string field, decimal expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var response = await client.GetAsync(Relative(path), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var value = await cache.HashGetAsync(key, "value");
            if (value.HasValue && JsonSerializer.Deserialize<JsonElement>((string)value!).GetProperty(field).GetDecimal() == expected) { return; }
            await Task.Delay(100, timeout.Token);
        }
    }

    private sealed class JourneyPricingApp(IReadOnlyDictionary<string, string> settings, GeneratedFileCertificates certificates) : WebApplicationFactory<PricingHostMarker>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key.Replace("__", ":", StringComparison.Ordinal), pair.Value))));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AgileConfig:AppId", string.Empty);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<HttpClient>();
                services.AddSingleton(_ => new HttpClient(certificates.CreateHandler()) { Timeout = TimeSpan.FromSeconds(5) });
            });
        }

        public new HttpClient CreateClient()
        {
            UseKestrel(0);
            return base.CreateClient();
        }
    }

    private sealed class CompletionReceiver(WebApplication app) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1);
        private readonly HashSet<Guid> _effects = [];
        public TaskCompletionSource<PricingRecalculationCompletedV1> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<Guid, int> Requests { get; } = new();
        public bool SawBearer { get; private set; }
        public Uri Address => new(app.Urls.Single());
        public int EffectCount(Guid id) => _effects.Contains(id) ? 1 : 0;

        public static async Task<CompletionReceiver> StartAsync(GeneratedFileCertificates certificates)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificates.ServerCertificate)));
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var receiver = new CompletionReceiver(app);
            app.MapPost("/webhooks/pricing", receiver.AcceptAsync);
            await app.StartAsync();
            return receiver;
        }

        private async Task AcceptAsync(HttpRequest request, HttpResponse response)
        {
            var message = await request.ReadFromJsonAsync<PricingRecalculationCompletedV1>();
            Assert.NotNull(message);
            Assert.Equal(message.IdempotencyKey.ToString("D"), request.Headers["Idempotency-Key"].ToString());
            await _gate.WaitAsync();
            try
            {
                SawBearer |= request.Headers.ContainsKey("Authorization");
                var count = Requests.AddOrUpdate(message.EventId, 1, (_, previous) => previous + 1);
                _effects.Add(message.EventId);
                Completion.TrySetResult(message);
                if (count == 1) { response.StatusCode = 500; return; }
                await response.WriteAsJsonAsync(new PricingWebhookAcceptedV1 { EventId = message.EventId, IdempotencyKey = message.IdempotencyKey, Accepted = true });
            }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            _gate.Dispose();
        }
    }
}

internal sealed class CostPricingRedisBrokerFactAttribute : FactAttribute
{
    public CostPricingRedisBrokerFactAttribute()
    {
        if (RabbitMqTestBroker.TryGetOptions() is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")))
        { Skip = "该旅程需要真实 PostgreSQL、RabbitMQ 和 Redis。"; }
    }
}
