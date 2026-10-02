using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Pricing.Application;
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
        services.AddPricingPostgres(connection, configuration.GetSection("Pricing:Tasks").Get<PricingTaskOptions>(), cache);
        if (cache?.Enabled == true) { services.AddPricingCacheInvalidationWorker(); }
        services.AddSingleton(new PricingConnection(connection));
        services.AddHostedService<PricingStartupCheck>();
        if (configuration.GetValue("Pricing:Worker:Enabled", true)) { services.AddHostedService<PricingWorker>(); }
        if (configuration.GetValue("Pricing:Messaging:Enabled", false))
        {
            var broker = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>()
                ?? throw new InvalidOperationException("必须配置 RabbitMq。");
            broker.Validate();
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
        return services;
    }

    /// <summary>映射需要 pricing-operator 策略的业务接口；执行协议不暴露给 HTTP。</summary>
    /// <param name="endpoints">路由。</param>
    /// <returns>路由。</returns>
    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/pricing").RequireAuthorization("pricing-operator")
            .ProducesApiErrors(400, 401, 403, 404, 409, 500);
        group.MapPost("/cost", async (UpdatePricingCost request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(request, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>(202).ProducesApiErrors(415);
        group.MapPost("/fee", async (UpdatePricingFee request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(request, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>(202).ProducesApiErrors(415);
        group.MapGet("/items/{itemId:guid}", async (Guid itemId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetPriceQuote(itemId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<PriceQuoteView>>().ProducesApiErrors(503);
        group.MapGet("/tasks/{taskId:guid}", async (Guid taskId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetRecalculation(taskId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>();
        group.MapPost("/tasks/{taskId:guid}/retry", async (Guid taskId, RetryRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryPricingWork(taskId, request.ExpectedEpoch), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<RecalculationStatus>>(202).ProducesApiErrors(415);
        return endpoints;
    }

    private static IResult Failure(Error error) => Results.Problem(title: error.Message,
        statusCode: error.Code switch
        {
            "pricing.not_found" => StatusCodes.Status404NotFound,
            "pricing.query_busy" or "pricing.query_timeout" => StatusCodes.Status503ServiceUnavailable,
            "pricing.request_conflict" or "pricing.version_conflict" or "pricing.retry_conflict" or "pricing.cost_owned_by_costing" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        }, extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

internal sealed record RetryRequest(long ExpectedEpoch);
internal sealed record PricingConnection(string Value);
