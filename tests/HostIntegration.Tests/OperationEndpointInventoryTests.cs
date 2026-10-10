using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using Xunit.Abstractions;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationEndpointInventoryTests(ITestOutputHelper output)
{
    private const string IdentityChanges = nameof(IdentityCommittedAuditTests) + "；身份状态与IdentityEntityCommittedV1同事务，拒绝/空操作不能虚构事实。";
    private const string FileChanges = nameof(FilesCommittedAuditTests) + "；文件状态与StoredFileCommittedV1同事务。";
    private const string PlanChanges = nameof(SchedulingCommittedAuditTests) + "；计划及裁决与PlanCommittedV1同事务，不等于目标任务完成。";
    private const string PolicyChanges = nameof(FactCapacityPolicyAuditIngestionTests) + "；所属容量治理事实含前后额度，不混入普通对象事实。";
    private const string RecoveryLifecycle = "技术控制例外：所属投递恢复凭据保存原裁决和恢复版本，不表示业务再次提交；原事实继续交付。";
    private const string TaskLifecycle = "任务生命周期例外：任务/批次/发生保留状态与尝试，执行操作被观察；实际应用成本或报价才产生对象变化事实。";
    private const string HealthProbe = "诊断例外：健康探测不提交领域变化，操作观察按框架路由显式排除；不限HTTP方法也须登记。";
    private const string GatewayRouteLifecycle = nameof(GatewayRouteAdminTests) + "；技术配置例外：网关保存并应用自身路由配置，记录操作观察，不伪造下游业务事实。";
    private static readonly Dictionary<string, Dictionary<string, string>> WriteDecisions = new(StringComparer.Ordinal)
    {
        ["platform"] = new(StringComparer.Ordinal)
        {
            ["* /health"] = HealthProbe,
            ["* /health/live"] = HealthProbe,
            ["* /health/logging"] = HealthProbe,
            ["* /health/ready"] = HealthProbe,
            ["PUT /api/platform/settings/{key}"] = nameof(AuditBusinessJourneyTests) + "；设置创建及变化同事务登记SettingCommittedV1。",
            ["POST /api/auditing/exports/"] = nameof(AuditExportJourneyTests) + "；调查导出技术生命周期：本人请求与冻结快照保存在同一聚合，不递归生成审计事实。",
            ["POST /api/auditing/exports/{exportId:guid}/cancel"] = nameof(AuditExportJourneyTests) + "；调查导出技术生命周期：取消原本人委托，保留原快照身份。",
            ["POST /api/auditing/exports/{exportId:guid}/retry"] = nameof(AuditExportJourneyTests) + "；调查导出技术生命周期：恢复原快照及发布身份。",
            ["DELETE /api/platform/settings/{key}"] = nameof(AuditBusinessJourneyTests) + "；清空值是设置变化，未变值无新事实。",
            ["POST /api/identity/users"] = IdentityChanges,
            ["POST /api/identity/roles"] = IdentityChanges,
            ["POST /api/identity/roles/{roleId:long}/menus/{menuId:long}"] = IdentityChanges,
            ["PUT /api/identity/roles/{roleId:long}/menus"] = IdentityChanges,
            ["POST /api/identity/users/{userId:long}/roles/{roleId:long}"] = IdentityChanges,
            ["POST /api/identity/users/{userId:long}/roles/{roleId:long}/revoke"] = IdentityChanges,
            ["POST /api/identity/me/password"] = IdentityChanges,
            ["POST /api/identity/users/{userId:long}/password"] = IdentityChanges,
            ["POST /api/identity/users/{userId:long}/disable"] = IdentityChanges,
            ["POST /api/identity/users/{userId:long}/enable"] = IdentityChanges,
            ["POST /api/identity/login"] = IdentityChanges,
            ["POST /api/identity/logout"] = IdentityChanges,
            ["POST /api/identity/refresh"] = IdentityChanges,
            ["POST /api/identity/api-resources"] = IdentityChanges,
            ["POST /api/identity/menus"] = IdentityChanges,
            ["POST /api/identity/authorize"] = "查询型POST：只计算授权结论，保留操作观察，不生成业务提交事实。",
            ["POST /api/files/"] = FileChanges,
            ["DELETE /api/files/{id:long}"] = FileChanges,
            ["POST /internal/files/v1/uploads/{uploadId:guid}"] = nameof(GeneratedFilesHttpsTests) + "；候选登记保存文件registered事实。",
            ["PUT /internal/files/v1/uploads/{uploadId:guid}/content"] = nameof(GeneratedFilesHttpsTests) + "；封存字节保存文件stored事实。",
            ["POST /internal/files/v1/uploads/{uploadId:guid}/publish"] = nameof(GeneratedFilesHttpsTests) + "；发布保存published事实，不等于当前永远可下载。",
            ["POST /api/scheduling/tasks/"] = PlanChanges,
            ["PUT /api/scheduling/tasks/{id:long}/rule"] = PlanChanges,
            ["POST /api/scheduling/tasks/{id:long}/pause"] = PlanChanges,
            ["POST /api/scheduling/tasks/{id:long}/resume"] = PlanChanges,
            ["POST /api/scheduling/tasks/preview"] = "查询型POST：仅预览计划，不保存计划/发生，不生成提交事实。",
            ["POST /api/scheduling/occurrences/{id:guid}/retry"] = TaskLifecycle,
            ["POST /api/platform/audit-deliveries/{messageId:guid}/retry"] = RecoveryLifecycle,
            ["POST /api/identity/audit-deliveries/{messageId:guid}/retry"] = RecoveryLifecycle,
            ["POST /api/files/audit-deliveries/{messageId:guid}/retry"] = RecoveryLifecycle,
            ["POST /api/scheduling/audit-deliveries/{messageId:guid}/retry"] = RecoveryLifecycle,
            ["PUT /api/platform/audit-capacity"] = PolicyChanges,
            ["PUT /api/identity/audit-capacity"] = PolicyChanges,
            ["PUT /api/files/audit-capacity"] = PolicyChanges,
            ["PUT /api/scheduling/audit-capacity"] = PolicyChanges,
        },
        ["costing"] = new(StringComparer.Ordinal)
        {
            ["* /health"] = HealthProbe,
            ["* /health/delivery"] = HealthProbe,
            ["* /health/live"] = HealthProbe,
            ["* /health/logging"] = HealthProbe,
            ["* /health/ready"] = HealthProbe,
            ["POST /api/costing/cost"] = nameof(CostingCommittedAuditTests) + "；输入与CostSheetCommittedV1同事务，异步计算另有结果事实。",
            ["POST /api/costing/batches"] = TaskLifecycle,
            ["POST /api/costing/batches/{batchId:guid}/cancel"] = TaskLifecycle,
            ["POST /api/costing/batches/{batchId:guid}/retry"] = TaskLifecycle,
            ["POST /api/costing/tasks/{taskId:guid}/cancel"] = TaskLifecycle,
            ["POST /api/costing/tasks/{taskId:guid}/retry"] = TaskLifecycle,
            ["POST /api/costing/tasks/{taskId:guid}/delivery/retry"] = TaskLifecycle,
            ["POST /api/costing/audit-deliveries/{messageId:guid}/retry"] = RecoveryLifecycle,
            ["PUT /api/costing/audit-capacity"] = PolicyChanges,
        },
        ["pricing"] = new(StringComparer.Ordinal)
        {
            ["* /health"] = HealthProbe,
            ["* /health/live"] = HealthProbe,
            ["* /health/logging"] = HealthProbe,
            ["* /health/ready"] = HealthProbe,
            ["POST /api/pricing/cost"] = "PricingAuditTests；输入与PriceQuoteCommittedV1同事务，空操作不产生新事实。",
            ["POST /api/pricing/fee"] = "PricingAuditTests；费率输入与PriceQuoteCommittedV1同事务，异步结果另行登记。",
            ["POST /api/pricing/exports/"] = nameof(PricingExportJourneyTests) + "；技术生命周期例外：持久委托/冻结快照/发布裁决，不冒充报价变化。",
            ["POST /api/pricing/exports/{exportId:guid}/cancel"] = nameof(PricingExportJourneyTests) + "；技术生命周期例外：原导出状态与版本，操作仍被观察。",
            ["POST /api/pricing/exports/{exportId:guid}/retry"] = nameof(PricingExportJourneyTests) + "；技术生命周期例外：恢复原委托，不伪造新报价提交。",
            ["POST /api/pricing/tasks/{taskId:guid}/cancel"] = TaskLifecycle,
            ["POST /api/pricing/tasks/{taskId:guid}/retry"] = TaskLifecycle,
            ["POST /api/pricing/audit-deliveries/{messageId:guid}/retry"] = RecoveryLifecycle,
            ["PUT /api/pricing/audit-capacity"] = PolicyChanges,
        },
        ["gateway"] = new(StringComparer.Ordinal)
        {
            ["* /health"] = HealthProbe,
            ["* /health/live"] = HealthProbe,
            ["* /health/logging"] = HealthProbe,
            ["* /health/ready"] = HealthProbe,
            ["* /hubs/gateway"] = "实时协议例外：连接不表示业务提交；本轮HTTP观察不代替实时消息规格验收。",
            ["* /hubs/gateway/negotiate"] = "实时协议例外：协商不表示业务提交；本轮HTTP观察不代替实时消息规格验收。",
            ["POST /gateway/routes/"] = GatewayRouteLifecycle,
            ["PUT /gateway/routes/{routeId}"] = GatewayRouteLifecycle,
            ["DELETE /gateway/routes/{routeId}"] = GatewayRouteLifecycle,
        },
    };
    private readonly List<string> _writeInventoryViolations = [];
    private readonly HashSet<string> _inspectedWriteSources = new(StringComparer.Ordinal);

    [PostgresFact]
    public async Task EveryRealHostEndpoint_IsObservedOrExplicitlyExcluded()
    {
        using var certificates = new GeneratedFileCertificates();
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using (var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true))
        {
            await CostingDatabase.MigrateAsync(database.ConnectionString);
            await PricingDatabase.MigrateAsync(database.ConnectionString);
        }
        await using var platform = new ProducerInventoryApp(certificates, database.ConnectionString);
        await InspectAsync(platform, "platform");
        await using var costing = new BusinessHostApp<CostingHostMarker>("Costing", database.ConnectionString);
        var costingRecords = await InspectAsync(costing, "costing");
        Assert.Contains(costingRecords, record => record.Metadata?.Action == "costing.cost.update");
        Assert.Contains(costingRecords, record => record.Metadata?.Action == "costing.task.retry"
            && record.Metadata.SubjectId == "98e26a25-036d-49cb-aaef-2e6197a33ce0" && record.Metadata.SubjectType == "CostCalculation");
        Assert.Contains(costingRecords, record => record.Metadata?.Action == "costing.task.cancel"
            && record.Metadata.SubjectId == "98e26a25-036d-49cb-aaef-2e6197a33ce0" && record.Metadata.SubjectType == "CostCalculation");
        await using (var scope = costing.Services.CreateAsyncScope())
        {
            var taskId = Guid.NewGuid();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new CancelCostingWork(taskId, 0))).IsFailure);
            Assert.True((await sender.SendAsync(new RenewCostingWork(taskId, 0))).IsFailure);
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var records = (await ReadAsync(journal)).Where(record => record.Metadata?.TaskId == taskId && record.Phase == "finished").ToArray();
            Assert.Equal(2, records.Length);
            Assert.All(records, record => Assert.Equal("rejected", record.Outcome));
        }
        var files = certificates.PricingClientOptions(new Uri("https://127.0.0.1:1/"));
        await using var pricing = new BusinessHostApp<PricingHostMarker>("Pricing", database.ConnectionString)
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                ["Pricing:Exports:Enabled"] = "true",
                ["Pricing:Exports:Worker:Enabled"] = "false",
                ["Pricing:Exports:Files:BaseAddress"] = files.BaseAddress,
                ["Pricing:Exports:Files:ClientCertificatePath"] = files.ClientCertificatePath,
                ["Pricing:Exports:Files:ClientKeyPath"] = files.ClientKeyPath,
                ["Pricing:Exports:Files:RootCertificatePaths:0"] = files.RootCertificatePaths[0],
                ["Pricing:Exports:Files:RevocationMode"] = "NoCheck",
            },
        };
        var pricingRecords = await InspectAsync(pricing, "pricing");
        Assert.Contains(pricingRecords, record => record.Metadata?.Action == "pricing.fee.update");
        Assert.Contains(pricingRecords, record => record.Metadata?.Action == "pricing.task.retry"
            && record.Metadata.SubjectId == "98e26a25-036d-49cb-aaef-2e6197a33ce0" && record.Metadata.SubjectType == "Recalculation");
        Assert.Contains(pricingRecords, record => record.Metadata?.Action == "pricing.task.cancel"
            && record.Metadata.SubjectId == "98e26a25-036d-49cb-aaef-2e6197a33ce0" && record.Metadata.SubjectType == "Recalculation");
        await using (var scope = pricing.Services.CreateAsyncScope())
        {
            var taskId = Guid.NewGuid();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new CancelPricingWork(taskId, 0))).IsFailure);
            Assert.True((await sender.SendAsync(new RenewPricingWork(taskId, 0))).IsFailure);
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var records = (await ReadAsync(journal)).Where(record => record.Metadata?.TaskId == taskId && record.Phase == "finished").ToArray();
            Assert.Equal(2, records.Length);
            Assert.All(records, record => Assert.Equal("rejected", record.Outcome));
        }
        await using var backend = await GatewayBackend.StartAsync("inventory");
        await using var gateway = new GatewayHttpApp(backend.Address);
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.business.json"))).Value;
        gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend" }));
        var gatewayRecords = await InspectAsync(gateway, "gateway");
        Assert.Contains(gatewayRecords, record => record.Metadata?.ExecutionRole == "proxy");
        Assert.Equal(WriteDecisions.Keys.Order(StringComparer.Ordinal), _inspectedWriteSources.Order(StringComparer.Ordinal));
        Assert.True(_writeInventoryViolations.Count == 0, string.Join(Environment.NewLine, _writeInventoryViolations));
    }

    private async Task<OperationObservedV1[]> InspectAsync<T>(WebApplicationFactory<T> host, string source) where T : class
    {
        using var client = host.CreateClient();
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        Assert.NotEmpty(endpoints);
        _inspectedWriteSources.Add(source);
        var writes = endpoints
                .Where(endpoint => endpoint.Metadata.GetMetadata<OperationDescription>()?.IsProxy != true) // 转发由下游拥有提交事实；网关自身管理入口仍须登记。
                .SelectMany(endpoint => AcceptedMethods(endpoint)
                    .Where(method => method is not ("GET" or "HEAD" or "OPTIONS"))
                    .Select(method => method + " " + endpoint.RoutePattern.RawText)).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(writes);
        Assert.True(WriteDecisions.TryGetValue(source, out var decisions), source + " 未登记宿主写入口审计义务。");
        Assert.All(decisions.Values, decision => Assert.False(string.IsNullOrWhiteSpace(decision)));
        var missing = writes.Except(decisions.Keys, StringComparer.Ordinal).ToArray();
        var stale = decisions.Keys.Except(writes, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0 || stale.Length != 0)
        {
            _writeInventoryViolations.Add(source + ": 未登记 [" + string.Join(", ", missing) + "]; 已删除 [" + string.Join(", ", stale) + "]");
        }
        await using var scope = host.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observed = new List<OperationObservedV1>();
        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText;
            Assert.False(string.IsNullOrWhiteSpace(route));
            if (route.StartsWith("/hubs", StringComparison.Ordinal)) { continue; } // 实时协议不属于本轮 HTTP 业务入口。
            var frameworkExcluded = route.StartsWith("/health", StringComparison.Ordinal) || route.StartsWith("/openapi", StringComparison.Ordinal)
                || route.StartsWith("/gateway/openapi", StringComparison.Ordinal);
            var suppression = endpoint.Metadata.GetMetadata<OperationLogSuppression>();
            if (route is "/api/auditing/entries" or "/api/auditing/operations" or "/api/auditing/capacity") { Assert.NotNull(suppression); }
            foreach (var method in AcceptedMethods(endpoint).SelectMany(method => method == "*" ? new[] { "GET", "POST" } : [method]))
            {
                var correlation = "inventory-" + Guid.NewGuid().ToString("N");
                using var request = new HttpRequestMessage(new HttpMethod(method), Expand(endpoint.RoutePattern));
                request.Headers.Add("X-Correlation-Id", correlation);
                using var response = await client.SendAsync(request);
                if (suppression is not null || frameworkExcluded)
                {
                    if (suppression is not null) { Assert.False(string.IsNullOrWhiteSpace(suppression.Reason)); }
                    Assert.DoesNotContain(await ReadAsync(journal), item => item.Metadata?.CorrelationId == correlation);
                    continue;
                }
                var finished = await WaitAsync(journal, correlation);
                Assert.Equal(source, finished.Source);
                Assert.Equal(route, finished.RouteTemplate);
                Assert.Equal((int)response.StatusCode, finished.StatusCode);
                Assert.Null(finished.ActorId);
                observed.Add(finished);
            }
        }
        Assert.NotEmpty(observed);
        foreach (var path in new[] { "/swagger/index.html", "/swagger/swagger-ui.css" })
        {
            var correlation = "static-" + Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Correlation-Id", correlation);
            using var response = await client.SendAsync(request);
            Assert.DoesNotContain(await ReadAsync(journal), item => item.Metadata?.CorrelationId == correlation);
        }
        output.WriteLine($"{source}: {endpoints.Length} real route endpoints; {observed.Count} business HTTP operations verified.");
        return [.. observed];
    }

    private static IReadOnlyList<string> AcceptedMethods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? methods : ["*"];

    internal static async Task<OperationObservedV1[]> ReadAsync(IOutboxStore journal) =>
        (await journal.ReadPendingAsync(1000, DateTimeOffset.UtcNow)).Select(entry =>
            new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(entry.Payload)).ToArray();

    internal static async Task<OperationObservedV1> WaitAsync(IOutboxStore journal, string correlation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var found = (await ReadAsync(journal)).Where(item => item.Metadata?.CorrelationId == correlation && item.Phase == "finished").ToArray();
            if (found.Length > 0) { return Assert.Single(found); }
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class ProducerInventoryApp(GeneratedFileCertificates certificates, string connectionString) : PersistentIdentityApp(connectionString, schedulingWorkerEnabled: false)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Testing");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var files = certificates.PricingClientOptions(new Uri("https://127.0.0.1:1"));
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auditing:Exports:Enabled"] = "true",
                ["Auditing:Exports:Worker:Enabled"] = "false",
                ["Auditing:Exports:Files:BaseAddress"] = files.BaseAddress,
                ["Auditing:Exports:Files:ClientCertificatePath"] = files.ClientCertificatePath,
                ["Auditing:Exports:Files:ClientKeyPath"] = files.ClientKeyPath,
                ["Auditing:Exports:Files:RootCertificatePaths:0"] = files.RootCertificatePaths[0],
                ["Auditing:Exports:Files:RevocationMode"] = "NoCheck",
            }));
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(certificates.Settings
                .Where(pair => pair.Key.StartsWith("Files__Producer__", StringComparison.Ordinal))
                .Select(pair => new KeyValuePair<string, string?>(pair.Key.Replace("__", ":", StringComparison.Ordinal), pair.Value))));
            return base.CreateHost(builder);
        }
    }

    private static Uri Expand(RoutePattern pattern) => new("/" + string.Join('/', pattern.PathSegments.Select(segment =>
        string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter when parameter.ParameterPolicies.Any(policy => policy.Content == "guid") => "98e26a25-036d-49cb-aaef-2e6197a33ce0",
            RoutePatternParameterPart parameter when parameter.ParameterPolicies.Any(policy => policy.Content is "int" or "long") => "1",
            _ => "inventory",
        })))), UriKind.Relative);

}
