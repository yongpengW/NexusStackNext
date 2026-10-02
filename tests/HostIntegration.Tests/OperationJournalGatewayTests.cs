using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationJournalGatewayTests
{
    [PostgresFact]
    public async Task JournalStorageOutage_KeepsBusinessTrafficAvailableThroughGateway_AndRecoveryPreservesDegradation()
    {
        await using var business = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(business.ConnectionString);
        await using var journal = await IdentityJourneyDatabase.CreateAsync();
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OperationJournal__Storage__Provider"] = "Postgres",
            ["ConnectionStrings__OperationJournal"] = journal.ConnectionString,
            ["RabbitMQ__HostName"] = string.Empty,
            ["Pricing__Worker__Enabled"] = "false",
            ["Pricing__Messaging__Enabled"] = "false",
            ["Pricing__Cache__Enabled"] = "false",
        };
        var unmigrated = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", business.ConnectionString);
        unmigrated.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (key, value) in settings) { unmigrated.Environment[key] = value; }
        var startup = await BusinessProcess.RunToExitAsync(unmigrated);
        Assert.Equal(1, startup.ExitCode);
        Assert.True(startup.Output.Contains("Pricing startup failed", StringComparison.Ordinal), "Missing controlled startup diagnostic.");

        // journal 的独立迁移命令不要求启动 HTTP、工作进程或 JWT 配置。
        var migration = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", business.ConnectionString);
        migration.ArgumentList.Add("migrate-operation-journal");
        migration.Environment["ConnectionStrings__OperationJournal"] = journal.ConnectionString;
        migration.Environment["Jwt__SigningKey"] = string.Empty;
        var migrated = await BusinessProcess.RunToExitAsync(migration);
        Assert.Equal(0, migrated.ExitCode);
        Assert.True(migrated.Output.Contains("OperationJournal migrations applied.", StringComparison.Ordinal), "Missing journal migration confirmation.");

        await using var pricing = await BusinessProcess.StartAsync(
            typeof(PricingHostMarker).Assembly.Location, "Pricing", business.ConnectionString, settings: settings);
        var routes = Path.Combine(Path.GetTempPath(), $"nsn-operation-journal-routes-{Guid.NewGuid():N}.json");
        try
        {
            await WritePricingRoutesAsync(routes, pricing.Client.BaseAddress!);
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes);
            gateway.Authenticate();
            await AssertReadinessAsync(pricing.Client, "Healthy");
            await AssertReadinessAsync(gateway.Client, "Healthy");

            // 只破坏本测试独立 journal 库的表；业务库、共享数据库及其他服务均不受影响。
            await SetJournalStorageAvailableAsync(journal.ConnectionString, available: false);
            try
            {
                var taskId = await SubmitCostThroughGatewayAsync(gateway.Client);
                // 产品摘除阈值为连续两次失败；间隔压到一秒，跨四轮仍须能经边缘读取真实任务。
                for (var probe = 0; probe < 4; probe++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    using var task = await gateway.Client.GetAsync(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.OK, task.StatusCode);
                    var result = await task.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal(taskId, result.GetProperty("data").GetProperty("taskId").GetGuid());
                }

                _ = await SubmitCostThroughGatewayAsync(gateway.Client);
                await AssertReadinessAsync(pricing.Client, "Healthy");
                await AssertLoggingDegradedAsync(pricing.Client);
                await AssertReadinessAsync(gateway.Client, "Healthy");
            }
            finally
            {
                await SetJournalStorageAvailableAsync(journal.ConnectionString, available: true);
            }

            _ = await SubmitCostThroughGatewayAsync(gateway.Client);
            await AssertJournalWritesRecoveredAsync(journal.ConnectionString);
            // 存储恢复不抹去故障期未保存的观察；独立诊断保留降级，业务就绪仍然健康。
            await AssertReadinessAsync(pricing.Client, "Healthy");
            await AssertLoggingDegradedAsync(pricing.Client);
            await AssertReadinessAsync(gateway.Client, "Healthy");

            // 慢故障与表不存在不同：ready 不得等待日志探测的五秒预算，否则先被网关两秒预算摘除。
            await using var blocked = new NpgsqlConnection(journal.ConnectionString);
            await blocked.OpenAsync();
            await using var transaction = await blocked.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("LOCK TABLE operation_journal.outbox IN ACCESS EXCLUSIVE MODE", blocked, transaction))
            {
                await hold.ExecuteNonQueryAsync();
            }
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            using (var ready = await pricing.Client.GetAsync(new Uri("/health/ready", UriKind.Relative), budget.Token))
            {
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            }
            await Task.Delay(TimeSpan.FromSeconds(3));
            _ = await SubmitCostThroughGatewayAsync(gateway.Client);
            await transaction.RollbackAsync();
        }
        finally
        {
            File.Delete(routes);
        }
    }

    private static async Task<Guid> SubmitCostThroughGatewayAsync(HttpClient gateway)
    {
        var requestId = Guid.NewGuid();
        using var response = await gateway.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { requestId, itemId = Guid.NewGuid(), expectedVersion = 0, cost = 80m, feeRate = 0.2m });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(requestId, accepted.GetProperty("data").GetProperty("taskId").GetGuid());
        return requestId;
    }

    private static async Task AssertReadinessAsync(HttpClient client, string expected)
    {
        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertLoggingDegradedAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/health/logging", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync());
    }

    internal static async Task SetJournalStorageAvailableAsync(string connectionString, bool available)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(available
            ? "ALTER TABLE operation_journal.outbox_unavailable RENAME TO outbox"
            : "ALTER TABLE operation_journal.outbox RENAME TO outbox_unavailable", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertJournalWritesRecoveredAsync(string connectionString)
    {
        var services = new ServiceCollection().AddOperationJournalPostgresStorage(connectionString);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // 通过公开交付端口确认恢复后的 Started / Finished 已可投递，避免仅凭 ALTER 成功推断恢复。
        while ((await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow, timeout.Token)).Count != 2)
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task WritePricingRoutesAsync(string path, Uri pricing)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Gateway"))) { root = root.Parent; }
        Assert.NotNull(root);
        var table = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(root.FullName, "src", "Gateway", "NexusStackNext.Gateway", "routes.pricing.json")))!;
        var clusters = table["clusters"]!.AsArray();
        Assert.NotEmpty(clusters);
        foreach (var cluster in clusters)
        {
            var destinations = cluster!["destinations"]!.AsArray();
            Assert.NotEmpty(destinations);
            // 原产品路由及认证策略不变；所有探测目标均替换为本次进程，避免访问开发环境。
            foreach (var destination in destinations) { destination!["address"] = pricing.ToString(); }
            cluster["healthCheck"] = new JsonObject { ["interval"] = "00:00:01" };
        }
        await File.WriteAllTextAsync(path, table.ToJsonString());
    }
}
