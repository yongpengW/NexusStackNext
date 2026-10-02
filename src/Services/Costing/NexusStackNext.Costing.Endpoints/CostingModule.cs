using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;

namespace NexusStackNext.Costing.Endpoints;

/// <summary>精简成本核算样板的装配与 HTTP 契约。</summary>
public static class CostingModule
{
    /// <summary>接入自己的数据库与可选后台执行；从不读取平台数据库。</summary>
    /// <param name="services">容器。</param>
    /// <param name="configuration">宿主配置。</param>
    /// <returns>容器。</returns>
    public static IServiceCollection AddCostingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connection = configuration.GetConnectionString("Costing");
        if (string.IsNullOrWhiteSpace(connection)) { throw new InvalidOperationException("必须配置 ConnectionStrings:Costing，使用独立业务数据库。"); }
        services.AddCostingPostgres(connection, configuration.GetSection("Costing:Tasks").Get<CostingTaskOptions>());
        services.AddSingleton(new CostingConnection(connection));
        services.AddHostedService<CostingStartupCheck>();
        if (configuration.GetValue("Costing:Worker:Enabled", true)) { services.AddHostedService<CostingWorker>(); }
        if (configuration.GetValue("Costing:Messaging:Enabled", false))
        {
            var broker = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>()
                ?? throw new InvalidOperationException("必须配置 RabbitMq。");
            broker.Validate();
            services.AddSingleton(configuration.GetSection("Costing:Delivery").Get<OutboxDeliveryOptions>() ?? new OutboxDeliveryOptions());
            services.AddNexusStackRabbitMqEventBus(broker);
            services.AddHealthChecks().AddAsyncCheck("costing-broker", async token =>
                await RabbitMqReadiness.IsReadyAsync(broker, token).ConfigureAwait(false)
                    ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("成本事件的 broker 或交换机不可用。"), tags: ["ready"]);
        }
        services.AddHealthChecks().AddAsyncCheck("costing-database", async cancellationToken =>
            await CostingDatabase.IsReadyAsync(connection, cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Costing 数据库不可用或需要迁移。"), tags: ["ready"]);
        return services;
    }

    /// <summary>映射需要 costing-operator 策略的业务接口；执行协议不暴露给 HTTP。</summary>
    /// <param name="endpoints">路由。</param>
    /// <returns>路由。</returns>
    public static IEndpointRouteBuilder MapCostingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/costing").RequireAuthorization("costing-operator")
            .ProducesApiErrors(400, 401, 403, 404, 409, 500);
        group.MapPost("/cost", async (UpdateCostInputs request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(request, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<CostCalculationStatus>>(202).ProducesApiErrors(415);
        group.MapGet("/tasks/{taskId:guid}/delivery", async (Guid taskId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetCostDelivery(taskId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<CostDeliveryStatus>>();
        group.MapPost("/tasks/{taskId:guid}/delivery/retry", async (Guid taskId, DeliveryRetryRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryCostDelivery(taskId, request.ExpectedDeadLetteredAt), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<CostDeliveryStatus>>(202).ProducesApiErrors(415);
        group.MapGet("/items/{itemId:guid}", async (Guid itemId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetCostSheet(itemId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<CostSheetView>>();
        group.MapGet("/tasks/{taskId:guid}", async (Guid taskId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetCostCalculation(taskId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<CostCalculationStatus>>();
        group.MapPost("/tasks/{taskId:guid}/retry", async (Guid taskId, RetryRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryCostingWork(taskId, request.ExpectedEpoch), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<CostCalculationStatus>>(202).ProducesApiErrors(415);
        return endpoints;
    }

    private static IResult Failure(Error error) => Results.Problem(title: error.Message,
        statusCode: error.Code switch
        {
            "costing.not_found" => StatusCodes.Status404NotFound,
            "costing.request_conflict" or "costing.version_conflict" or "costing.retry_conflict" or "costing.delivery_conflict" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        }, extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

internal sealed record RetryRequest(long ExpectedEpoch);
internal sealed record CostingConnection(string Value);
internal sealed record DeliveryRetryRequest(DateTimeOffset ExpectedDeadLetteredAt);
