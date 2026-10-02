using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.Scheduling.Contracts;
using Npgsql;
using RabbitMQ.Client;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class BusinessCooperationTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [BusinessJourneyFact]
    public async Task GatewayCostUpdate_CrossesTwoDatabases_AndDuplicateDeliveryAfterRestartHasNoEffect()
    {
        var pricingDatabase = new CostingDatabaseFixture { MigrateOnInitialize = false };
        await pricingDatabase.InitializeAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-exchange", ClientName = prefix };
        var subscription = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription, new EventSubscription { EventName = ScheduleTriggeredV1.Name, ConsumerName = subscription.ConsumerName + "-schedules" }]);
        var routePath = Path.Combine(Path.GetTempPath(), $"nsn-business-routes-{Guid.NewGuid():N}.json");
        try
        {
            await PricingDatabase.MigrateAsync(pricingDatabase.ConnectionString);
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = Settings(broker, subscription.ConsumerName);
            var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = 0, purchaseCost = 80m, freightCost = 20m };
            // 先只完成本地计算，进程被终止时 Outbox 仍待发送。
            await using (var costing = await StartCostingAsync(settings: null))
            {
                costing.Authenticate();
                using var accepted = await costing.Client.PostAsJsonAsync(Relative("/api/costing/cost"), request);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                await WaitForAsync(costing.Client, $"/api/costing/tasks/{request.requestId}/delivery", data => data.GetProperty("state").GetString() == "Pending");
            }

            JsonElement before;
            await using (var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, worker: true, settings: settings))
            await using (var costing = await StartCostingAsync(settings))
            {
                await WriteRoutesAsync(routePath, costing.Client.BaseAddress!, pricing.Client.BaseAddress!);
                await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath);
                using var denied = await gateway.Client.GetAsync(Relative($"/api/costing/items/{request.itemId}"));
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                gateway.Authenticate();
                before = await WaitForAsync(gateway.Client, $"/api/pricing/items/{request.itemId}", data =>
                    data.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number && data.GetProperty("breakEvenPrice").GetDecimal() == 100m);
                Assert.Equal(1, before.GetProperty("costingRevision").GetInt64());
                await WaitForAsync(gateway.Client, $"/api/costing/tasks/{request.requestId}/delivery", data => data.GetProperty("state").GetString() == "Delivered");
                var cost = await WaitForAsync(gateway.Client, $"/api/costing/items/{request.itemId}", _ => true);
                using var costChanged = await gateway.Client.PostAsJsonAsync(Relative("/api/costing/cost"), new
                {
                    requestId = Guid.NewGuid(),
                    request.itemId,
                    expectedVersion = cost.GetProperty("version").GetInt64(),
                    purchaseCost = 90m,
                    freightCost = 10m,
                });
                Assert.Equal(HttpStatusCode.Accepted, costChanged.StatusCode);
                before = await WaitForAsync(gateway.Client, $"/api/pricing/items/{request.itemId}", data => data.GetProperty("costingRevision").GetInt64() == 2);
                var fee = new { requestId = Guid.NewGuid(), request.itemId, expectedVersion = before.GetProperty("version").GetInt64(), feeRate = 0.2m };
                using var changed = await gateway.Client.PostAsJsonAsync(Relative("/api/pricing/fee"), fee);
                Assert.Equal(HttpStatusCode.Accepted, changed.StatusCode);
                before = await WaitForAsync(gateway.Client, $"/api/pricing/items/{request.itemId}", data => data.GetProperty("breakEvenPrice").GetDecimal() == 125m);
            }

            // 消费方重启后，同一消息及更旧的来源版本仍不能再改业务状态。
            await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, worker: true, settings: settings);
            restarted.Authenticate();
            await using var bus = new RabbitMqEventBus(broker);
            var duplicate = Envelope(request.itemId, 1, 100m, request.requestId);
            Assert.True((await bus.PublishAsync(duplicate)).IsSuccess);
            // 新版本消息充当队列消费屏障；先验证旧 ID 没有把任务重复创建或回滚结果。
            var next = Envelope(request.itemId, 3, 120m, Guid.NewGuid());
            Assert.True((await bus.PublishAsync(next)).IsSuccess);
            await WaitForAsync(restarted.Client, $"/api/pricing/items/{request.itemId}", data => data.GetProperty("costingRevision").GetInt64() == 3
                && data.GetProperty("breakEvenPrice").GetDecimal() == 150m);
            Assert.True((await bus.PublishAsync(Envelope(request.itemId, 1, 10m, Guid.NewGuid()))).IsSuccess);
            Assert.True((await bus.PublishAsync(Envelope(request.itemId, 4, 120m, Guid.NewGuid()))).IsSuccess);
            var after = await WaitForAsync(restarted.Client, $"/api/pricing/items/{request.itemId}", data => data.GetProperty("costingRevision").GetInt64() == 4);
            Assert.Equal(120m, after.GetProperty("cost").GetDecimal());
            Assert.Equal(0.2m, after.GetProperty("feeRate").GetDecimal());
            Assert.Equal(before.GetProperty("version").GetInt64() + 3, after.GetProperty("version").GetInt64());
        }
        finally
        {
            File.Delete(routePath);
            await pricingDatabase.DisposeAsync();
            await DeleteTopologyAsync(broker, topology);
        }
    }

    [BusinessJourneyFact]
    public async Task ConsumerKilledDuringTaskRegistration_RollsBackInbox_AndBrokerRedeliversAfterRestart()
    {
        var pricingDatabase = new CostingDatabaseFixture { MigrateOnInitialize = false };
        await pricingDatabase.InitializeAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-exchange", ClientName = prefix };
        var subscription = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription, new EventSubscription { EventName = ScheduleTriggeredV1.Name, ConsumerName = subscription.ConsumerName + "-schedules" }]);
        try
        {
            await PricingDatabase.MigrateAsync(pricingDatabase.ConnectionString);
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = Settings(broker, subscription.ConsumerName);
            var message = Envelope(Guid.NewGuid(), 1, 100m, Guid.NewGuid());
            var item = new SystemTextJsonIntegrationEventSerializer().Deserialize<CostCalculatedV1>(message.Payload).ItemId;
            await using (var pause = await PauseRegistrationAsync(pricingDatabase.ConnectionString))
            await using (var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, worker: true, settings: settings))
            {
                pricing.Authenticate();
                await using var bus = new RabbitMqEventBus(broker);
                Assert.True((await bus.PublishAsync(message)).IsSuccess);
                await WaitUntilRegistrationBlockedAsync(pricingDatabase.ConnectionString);
                using var absent = await pricing.Client.GetAsync(Relative($"/api/pricing/tasks/{message.MessageId}"));
                Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
                // Dispose 杀掉真实进程；Inbox 已插入但整个事务尚未提交。
            }
            await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, worker: true, settings: settings);
            restarted.Authenticate();
            var result = await WaitForAsync(restarted.Client, $"/api/pricing/items/{item}", data => data.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number);
            Assert.Equal(100m, result.GetProperty("breakEvenPrice").GetDecimal());
            var task = await WaitForAsync(restarted.Client, $"/api/pricing/tasks/{message.MessageId}", data => data.GetProperty("state").GetString() == "Succeeded");
            Assert.Single(task.GetProperty("history").EnumerateArray());
        }
        finally
        {
            await pricingDatabase.DisposeAsync();
            await DeleteTopologyAsync(broker, topology);
        }
    }

    private static async Task<NpgsqlConnection> PauseRegistrationAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT pg_advisory_lock(798143);
                CREATE FUNCTION pricing.pause_registration() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_advisory_xact_lock(798143); RETURN NEW; END $$;
                CREATE TRIGGER pause_registration BEFORE INSERT ON pricing.tasks
                FOR EACH ROW EXECUTE FUNCTION pricing.pause_registration();
                """, connection);
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task WaitUntilRegistrationBlockedAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            // 仅确认故障屏障已命中；业务结果仍通过 HTTP 验证。
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory'", connection);
            if ((long)(await command.ExecuteScalarAsync(timeout.Token))! > 0) { return; }
            await Task.Delay(100, timeout.Token);
        }
    }

    private Task<BusinessProcess> StartCostingAsync(IReadOnlyDictionary<string, string>? settings) =>
        BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, worker: true, settings: settings);

    [BusinessJourneyFact]
    public async Task BrokerOutage_PreservesCompletedCost_AndOperatorCanRedriveTheSameEvent()
    {
        var pricingDatabase = new CostingDatabaseFixture { MigrateOnInitialize = false };
        await pricingDatabase.InitializeAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-exchange", ClientName = prefix };
        var subscription = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription, new EventSubscription { EventName = ScheduleTriggeredV1.Name, ConsumerName = subscription.ConsumerName + "-schedules" }]);
        try
        {
            await PricingDatabase.MigrateAsync(pricingDatabase.ConnectionString);
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = Settings(broker, subscription.ConsumerName);
            // 占用一个不会讲 AMQP 的本机端口，模拟 broker 故障，不影响共享 broker。
            using var unavailable = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            unavailable.Start();
            var port = ((IPEndPoint)unavailable.LocalEndpoint).Port;
            unavailable.Stop();
            var broken = Settings(broker with { HostName = "127.0.0.1", Port = port }, subscription.ConsumerName);
            broken["Costing__Delivery__MaxAttempts"] = "1";
            var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = 0, purchaseCost = 80m, freightCost = 20m };
            JsonElement failed;
            await using (var costing = await StartCostingAsync(broken))
            {
                costing.Authenticate();
                using var accepted = await costing.Client.PostAsJsonAsync(Relative("/api/costing/cost"), request);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                failed = await WaitForAsync(costing.Client, $"/api/costing/tasks/{request.requestId}/delivery", data => data.GetProperty("state").GetString() == "DeadLettered");
                var cost = await WaitForAsync(costing.Client, $"/api/costing/items/{request.itemId}", data => data.GetProperty("unitCost").ValueKind == JsonValueKind.Number);
                Assert.Equal(100m, cost.GetProperty("unitCost").GetDecimal());
                using var live = await costing.Client.GetAsync(Relative("/health/live"));
                using var ready = await costing.Client.GetAsync(Relative("/health/ready"));
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            }
            await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, worker: true, settings: settings);
            await using var recovered = await StartCostingAsync(settings);
            recovered.Authenticate();
            pricing.Authenticate();
            using var retried = await recovered.Client.PostAsJsonAsync(Relative($"/api/costing/tasks/{request.requestId}/delivery/retry"), new
            {
                expectedDeadLetteredAt = failed.GetProperty("deadLetteredAt").GetDateTimeOffset(),
            });
            Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
            await WaitForAsync(recovered.Client, $"/api/costing/tasks/{request.requestId}/delivery", data => data.GetProperty("state").GetString() == "Delivered");
            var result = await WaitForAsync(pricing.Client, $"/api/pricing/tasks/{request.requestId}", data => data.GetProperty("state").GetString() == "Succeeded");
            Assert.Equal(request.requestId, result.GetProperty("taskId").GetGuid());
        }
        finally
        {
            await pricingDatabase.DisposeAsync();
            await DeleteTopologyAsync(broker, topology);
        }
    }

    private static Dictionary<string, string> Settings(RabbitMqOptions broker, string consumerName) => new(StringComparer.Ordinal)
    {
        ["RabbitMq__HostName"] = broker.HostName,
        ["RabbitMq__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["RabbitMq__UserName"] = broker.UserName,
        ["RabbitMq__Password"] = broker.Password,
        ["RabbitMq__VirtualHost"] = broker.VirtualHost,
        ["RabbitMq__ExchangeName"] = broker.ExchangeName,
        ["RabbitMq__ClientName"] = broker.ClientName,
        ["Costing__Messaging__Enabled"] = "true",
        ["Costing__Scheduling__ConsumerName"] = consumerName + "-schedules",
        ["Pricing__Messaging__Enabled"] = "true",
        ["Pricing__Messaging__ConsumerName"] = consumerName,
        ["Costing__Delivery__PollInterval"] = "00:00:00.100",
    };

    private static EventEnvelope Envelope(Guid item, long revision, decimal cost, Guid id) => OutboxEntry.From(new CostCalculatedV1
    {
        EventId = id,
        ItemId = item,
        CostRevision = revision,
        UnitCost = cost,
        OccurredAt = DateTimeOffset.UtcNow,
    }, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope();

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> WaitForAsync(HttpClient client, string path, Func<JsonElement, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var response = await client.GetAsync(Relative(path), timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                var data = result.GetProperty("data");
                if (predicate(data)) { return data.Clone(); }
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task WriteRoutesAsync(string path, Uri costing, Uri pricing)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NexusStackNext.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var table = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src/Gateway/NexusStackNext.Gateway/routes.business.json")))!;
        foreach (var cluster in table["clusters"]!.AsArray())
        {
            var address = cluster!["clusterId"]!.GetValue<string>() == "costing-host" ? costing : pricing;
            foreach (var destination in cluster["destinations"]!.AsArray()) { destination!["address"] = address.ToString(); }
        }
        await File.WriteAllTextAsync(path, table.ToJsonString());
    }

    private static async Task DeleteTopologyAsync(RabbitMqOptions options, EventTopology topology)
    {
        var factory = new ConnectionFactory { HostName = options.HostName, Port = options.Port, UserName = options.UserName, Password = options.Password, VirtualHost = options.VirtualHost };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in topology.AllQueueNames) { await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false); }
        await channel.ExchangeDeleteAsync(topology.ExchangeName);
    }
}

internal sealed class BusinessJourneyFactAttribute : FactAttribute
{
    public BusinessJourneyFactAttribute()
    {
        if (RabbitMqTestBroker.TryGetOptions() is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable)))
        {
            Skip = "该旅程需要真实 PostgreSQL 和 RabbitMQ。";
        }
    }
}
