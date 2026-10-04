using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.Pricing.Endpoints;

/// <summary>精简定价样板的装配与 HTTP 契约。</summary>
public static class PricingModule
{
    /// <summary>接入自己的数据库与可选后台执行；从不读取平台数据库。</summary>
    /// <param name="services">容器。</param>
    /// <param name="configuration">宿主配置。</param>
    /// <returns>容器。</returns>
    public static IServiceCollection AddPricingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connection = configuration.GetConnectionString("Pricing");
        if (string.IsNullOrWhiteSpace(connection)) { throw new InvalidOperationException("必须配置 ConnectionStrings:Pricing，使用独立业务数据库。"); }
        var cache = configuration.GetSection("Pricing:Cache").Get<PricingCacheOptions>();
        var capacityWrite = configuration.GetSection("Pricing:AuditDelivery:CapacityWrite").Get<CommittedFactCapacityWriteOptions>() ?? new();
        connection = capacityWrite.ConfigureConnection(connection);
        services.AddPricingPostgres(connection, configuration.GetSection("Pricing:Tasks").Get<PricingTaskOptions>(), cache);
        var capacityRead = configuration.GetSection("Pricing:AuditDelivery:CapacityRead").Get<CommittedFactCapacityReadOptions>() ?? new();
        capacityRead.Validate();
        services.AddPricingFactCapacityReader(capacityRead);
        services.AddKeyedScoped<PostgresFactCapacityPolicyStore>("pricing", (provider, _) => new(
            connection, provider.GetRequiredService<IIntegrationEventSerializer>(), capacityRead.Timeout,
            new("pricing", PricingFactCapacityPolicyChangedV1.From)));
        services.AddKeyedScoped<ICommittedFactCapacityPolicyStore>("pricing", (provider, _) => provider.GetRequiredKeyedService<PostgresFactCapacityPolicyStore>("pricing"));
        services.AddKeyedScoped<ICommittedFactCapacityPolicyCleanup>("pricing", (provider, _) => provider.GetRequiredKeyedService<PostgresFactCapacityPolicyStore>("pricing"));
        if (cache?.Enabled == true) { services.AddPricingCacheInvalidationWorker(); }
        services.AddSingleton(new PricingConnection(connection));
        services.AddHostedService<PricingStartupCheck>();
        services.AddPricingFactCleanup(configuration.GetSection("Pricing:AuditDelivery:Cleanup").Get<CommittedFactCleanupOptions>());
        if (configuration.GetValue("Pricing:Worker:Enabled", true)) { services.AddHostedService<PricingWorker>(); }
        if (configuration.GetValue("Pricing:Messaging:Enabled", false))
        {
            var broker = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>()
                ?? throw new InvalidOperationException("必须配置 RabbitMq。");
            broker.Validate();
            services.AddSingleton(configuration.GetSection("Pricing:Delivery").Get<OutboxDeliveryOptions>() ?? new OutboxDeliveryOptions());
            services.AddNexusStackRabbitMqEventBus(broker);
            var consumer = configuration.GetValue<string>("Pricing:Messaging:ConsumerName") ?? "pricing-cost";
            ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = consumer });
            services.AddHealthChecks().AddAsyncCheck("pricing-broker", async token =>
                await RabbitMqReadiness.IsReadyAsync(broker, token).ConfigureAwait(false)
                    ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("成本事件的 broker 或交换机不可用。"), tags: ["ready"]);
        }
        services.AddHealthChecks().AddAsyncCheck("pricing-database", async cancellationToken =>
            await PricingDatabase.IsReadyAsync(connection, cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Pricing 数据库不可用或需要迁移。"), tags: ["ready"]);
        return services.AddCommittedFactPolicyMaintenance("pricing", configuration.GetSection("Pricing:AuditDelivery:PolicyMaintenance")
            .Get<FactCapacityPolicyMaintenanceOptions>());
    }

    /// <summary>映射需要 pricing-operator 策略的业务接口；执行协议不暴露给 HTTP。</summary>
    /// <param name="endpoints">路由。</param>
    /// <returns>路由。</returns>
    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/pricing").RequireAuthorization("pricing-operator")
            .ProducesApiErrors(400, 401, 403, 404, 409, 500);
        group.MapGet("/audit-capacity", async ([FromKeyedServices("pricing")] ICommittedFactCapacityPolicyStore policies,
            ApiResponses responses, CancellationToken token) =>
        {
            var result = await policies.ReadPolicyAsync(token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<FactCapacityPolicySnapshot>>().ProducesApiErrors(503);
        group.MapPut("/audit-capacity", async (FactCapacityPolicyRequest request, ICurrentUser user, IClock clock,
            IExecutionContext execution, ApiResponses responses, CancellationToken token,
            [FromKeyedServices("pricing")] ICommittedFactCapacityPolicyStore policies) =>
        {
            var result = await policies.AdjustAsync(request, user.UserId ?? string.Empty, clock.UtcNow,
                execution.Capture(), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<FactCapacityPolicyReceipt>>().ProducesApiErrors(400, 409, 415, 503)
            .WithMetadata(new OperationDescription("pricing.fact-capacity-policy.adjust", "调整所属事实容量策略"));
        group.MapPost("/cost", async (UpdatePricingCost request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(request, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>(202).ProducesApiErrors(415, 503);
        group.MapPost("/fee", async (UpdatePricingFee request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(request, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>(202).ProducesApiErrors(415, 503)
            .WithMetadata(new OperationDescription("pricing.fee.update", "更新费率并申请重算"));
        group.MapGet("/items/{itemId:guid}", async (Guid itemId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetPriceQuote(itemId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<PriceQuoteView>>().ProducesApiErrors(503);
        group.MapGet("/tasks", async (int? page, int? limit, string? state, Guid? itemId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var query = new ListRecalculations(page ?? 1, limit ?? 50, state, itemId);
            var result = await sender.QueryAsync(query, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Page(result.Value.Items, result.Value.Total, new ApiPageRequest(query.Page, query.Limit)) : Failure(result.Error);
        }).Produces<ApiPage<RecalculationSummary>>();
        group.MapPost("/tasks/{taskId:guid}/cancel", async (Guid taskId, CancelRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new CancelPricingWork(taskId, request.ExpectedEpoch), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>().ProducesApiErrors(415)
            .WithMetadata(new OperationDescription("pricing.task.cancel", "取消计算任务",
                new OperationSubjectRoute("Recalculation", "taskId", OperationSubjectIdKind.Uuid)));
        group.MapGet("/tasks/{taskId:guid}", async (Guid taskId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetRecalculation(taskId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>();
        group.MapPost("/tasks/{taskId:guid}/retry", async (Guid taskId, RetryRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryPricingWork(taskId, request.ExpectedEpoch), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>(202).ProducesApiErrors(415)
            .WithMetadata(new OperationDescription("pricing.task.retry", "重试定价计算",
                new OperationSubjectRoute("Recalculation", "taskId", OperationSubjectIdKind.Uuid)));
        return endpoints;
    }

    private static IResult Failure(Error error) => Results.Problem(title: error.Message,
        statusCode: error.Code switch
        {
            "pricing.not_found" => StatusCodes.Status404NotFound,
            "pricing.audit_policy.control_exhausted" or "pricing.query_busy" or "pricing.query_timeout" or "pricing.audit_capacity_exhausted" or "audit_capacity.unavailable" or "audit_capacity.busy" => StatusCodes.Status503ServiceUnavailable,
            "pricing.audit_policy.conflict" or "pricing.request_conflict" or "pricing.version_conflict" or "pricing.retry_conflict" or "pricing.cost_owned_by_costing" or "pricing.cancel_conflict" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        }, extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

internal sealed record RetryRequest(long ExpectedEpoch);
internal sealed record CancelRequest([property: JsonRequired] long ExpectedEpoch);
internal sealed record PricingConnection(string Value);
