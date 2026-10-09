using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
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
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.Costing.Endpoints;

/// <summary>精简成本核算样板的装配与 HTTP 契约。</summary>
public static class CostingModule
{
    /// <summary>可靠交付依赖的独立诊断；MQ 故障不阻止本地持久受理。</summary>
    public const string DeliveryHealthTag = "costing-delivery";
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
        var capacityWrite = configuration.GetSection("Costing:AuditDelivery:CapacityWrite").Get<CommittedFactCapacityWriteOptions>() ?? new();
        connection = capacityWrite.ConfigureConnection(connection);
        services.AddCostingPostgres(connection, configuration.GetSection("Costing:Tasks").Get<CostingTaskOptions>(),
            configuration.GetSection("Costing:Batches").Get<CostingBatchOptions>());
        var capacityRead = configuration.GetSection("Costing:AuditDelivery:CapacityRead").Get<CommittedFactCapacityReadOptions>() ?? new();
        capacityRead.Validate();
        services.AddCostingFactCapacityReader(capacityRead);
        services.AddKeyedScoped<PostgresFactCapacityPolicyStore>("costing", (provider, _) => new(
            connection, provider.GetRequiredService<IIntegrationEventSerializer>(), capacityRead.Timeout,
            new("costing", CostingFactCapacityPolicyChangedV1.From)));
        services.AddKeyedScoped<ICommittedFactCapacityPolicyStore>("costing", (provider, _) => provider.GetRequiredKeyedService<PostgresFactCapacityPolicyStore>("costing"));
        services.AddKeyedScoped<ICommittedFactCapacityPolicyCleanup>("costing", (provider, _) => provider.GetRequiredKeyedService<PostgresFactCapacityPolicyStore>("costing"));
        services.AddSingleton(new CostingConnection(connection));
        services.AddHostedService<CostingStartupCheck>();
        services.AddCostingFactCleanup(configuration.GetSection("Costing:AuditDelivery:Cleanup").Get<CommittedFactCleanupOptions>());
        if (configuration.GetValue("Costing:Worker:Enabled", true))
        {
            services.AddHostedService<CostingWorker>();
            services.AddHostedService<CostBatchWorker>();
        }
        if (configuration.GetValue("Costing:Messaging:Enabled", false))
        {
            var broker = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>()
                ?? throw new InvalidOperationException("必须配置 RabbitMq。");
            broker.Validate();
            services.AddSingleton(configuration.GetSection("Costing:Delivery").Get<OutboxDeliveryOptions>() ?? new OutboxDeliveryOptions());
            services.AddNexusStackRabbitMqEventBus(broker);
            if (configuration.GetValue("Costing:Scheduling:Enabled", true))
            {
                var consumer = configuration.GetValue<string>("Costing:Scheduling:ConsumerName") ?? "costing-schedules";
                ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
                services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = ScheduleTriggeredV1.Name, ConsumerName = consumer });
            }
            services.AddHealthChecks().AddAsyncCheck("costing-broker", async token =>
                await RabbitMqReadiness.IsReadyAsync(broker, token).ConfigureAwait(false)
                    ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("成本事件的 broker 或交换机不可用。"), tags: [DeliveryHealthTag]);
        }
        services.AddHealthChecks().AddAsyncCheck("costing-database", async cancellationToken =>
            await CostingDatabase.IsReadyAsync(connection, cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Costing 数据库不可用或需要迁移。"), tags: ["ready"]);
        return services.AddCommittedFactPolicyMaintenance("costing", configuration.GetSection("Costing:AuditDelivery:PolicyMaintenance")
            .Get<FactCapacityPolicyMaintenanceOptions>())
            .AddFactDeliveryRecoveryMaintenance<ICostingAuditDelivery>("costing",
                configuration.GetSection("Costing:AuditDelivery:RecoveryMaintenance").Get<FactDeliveryRecoveryMaintenanceOptions>());
    }

    /// <summary>映射需要 costing-operator 策略的业务接口；执行协议不暴露给 HTTP。</summary>
    /// <param name="endpoints">路由。</param>
    /// <returns>路由。</returns>
    public static IEndpointRouteBuilder MapCostingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/costing").RequireAuthorization("costing-operator")
            .ProducesApiErrors(400, 401, 403, 404, 409, 500, 503);
        group.MapPost("/batches", async (HttpRequest http, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
            ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var parsed = await CostBatchJson.ReadAsync(http, json.Value.SerializerOptions, token).ConfigureAwait(false);
            var validation = parsed.Validation;
            if (validation.ErrorCount != 0)
            {
                return Results.Problem(title: "批次原始输入无效。", statusCode: 400, extensions: new Dictionary<string, object?>
                { ["errorCode"] = "costing.batch.invalid_input", ["errorCount"] = validation.ErrorCount, ["errors"] = validation.Errors });
            }
            var result = await sender.SendAsync(parsed.Request!, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/batches", "POST").Accepts<AcceptCostBatch>("application/json").Produces<ApiResponse<CostBatchStatus>>(202).ProducesApiErrors(413, 415)
            .WithMetadata(new OperationDescription("costing.batch.accept", "受理成本批次"));
        group.MapGet("/batches", async (int? page, int? limit, string? state, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var query = new ListCostBatches(page ?? 1, limit ?? 50, state);
            var result = await sender.QueryAsync(query, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Page(result.Value.Items, result.Value.Total, new ApiPageRequest(query.Page, query.Limit)) : Failure(result.Error);
        }).RequirePermission("/api/costing/batches", "GET").Produces<ApiPage<CostBatchStatus>>();
        group.MapGet("/batches/{batchId:guid}", async (Guid batchId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetCostBatch(batchId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/batches/{batchId}", "GET").Produces<ApiResponse<CostBatchStatus>>();
        group.MapGet("/batches/{batchId:guid}/rows", async (Guid batchId, int? page, int? limit, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var query = new ListCostBatchRows(batchId, page ?? 1, limit ?? 50);
            var result = await sender.QueryAsync(query, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Page(result.Value.Items, result.Value.Total, new ApiPageRequest(query.Page, query.Limit)) : Failure(result.Error);
        }).RequirePermission("/api/costing/batches/{batchId}/rows", "GET").Produces<ApiPage<CostBatchRowStatus>>();
        group.MapGet("/batches/{batchId:guid}/attempts", async (Guid batchId, int? page, int? limit, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var query = new ListCostBatchAttempts(batchId, page ?? 1, limit ?? 50);
            var result = await sender.QueryAsync(query, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Page(result.Value.Items, result.Value.Total, new ApiPageRequest(query.Page, query.Limit)) : Failure(result.Error);
        }).RequirePermission("/api/costing/batches/{batchId}/attempts", "GET").Produces<ApiPage<CostingAttempt>>();
        group.MapPost("/batches/{batchId:guid}/retry", async (Guid batchId, CancelRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryCostBatch(batchId, request.ExpectedEpoch), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/batches/{batchId}/retry", "POST").Produces<ApiResponse<CostBatchStatus>>(202).ProducesApiErrors(415)
            .WithMetadata(new OperationDescription("costing.batch.retry", "重试批次导入",
                new OperationSubjectRoute("CostBatch", "batchId", OperationSubjectIdKind.Uuid)));
        group.MapPost("/batches/{batchId:guid}/cancel", async (Guid batchId, CancelRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new CancelCostBatch(batchId, request.ExpectedEpoch), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/batches/{batchId}/cancel", "POST").Produces<ApiResponse<CostBatchStatus>>().ProducesApiErrors(415)
            .WithMetadata(new OperationDescription("costing.batch.cancel", "取消批次导入",
                new OperationSubjectRoute("CostBatch", "batchId", OperationSubjectIdKind.Uuid)));
        group.MapGet("/audit-capacity", async ([FromKeyedServices("costing")] ICommittedFactCapacityPolicyStore policies,
            ApiResponses responses, CancellationToken token) =>
        {
            var result = await policies.ReadPolicyAsync(token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/audit-capacity", "GET").Produces<ApiResponse<FactCapacityPolicySnapshot>>().ProducesApiErrors(503);
        group.MapPut("/audit-capacity", async (FactCapacityPolicyRequest request, ICurrentUser user, IClock clock,
            IExecutionContext execution, ApiResponses responses, CancellationToken token,
            [FromKeyedServices("costing")] ICommittedFactCapacityPolicyStore policies) =>
        {
            var result = await policies.AdjustAsync(request, user.UserId ?? string.Empty, clock.UtcNow,
                execution.Capture(), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/audit-capacity", "PUT").Produces<ApiResponse<FactCapacityPolicyReceipt>>().ProducesApiErrors(400, 409, 415, 503)
            .WithMetadata(new OperationDescription("costing.fact-capacity-policy.adjust", "调整所属事实容量策略"));
        group.MapGet("/audit-deliveries", async ([FromServices] ICostingAuditDelivery delivery,
            ApiResponses responses, CancellationToken token, string state = "Pending", int limit = 50) =>
        {
            if (state is not ("Pending" or "Delivered" or "DeadLettered") || limit is < 1 or > 100)
            { return Failure(new Error("costing.delivery_query.invalid", "投递状态必须为 Pending、Delivered 或 DeadLettered，limit 必须在 1 到 100。")); }
            return (IResult)responses.Ok(await delivery.ListAsync(state, limit, token).ConfigureAwait(false));
        }).RequirePermission("/api/costing/audit-deliveries", "GET")
            .Produces<ApiResponse<IReadOnlyList<FactDeliveryState>>>();

        group.MapGet("/audit-deliveries/{messageId:guid}", async (Guid messageId,
            [FromServices] ICostingAuditDelivery delivery, ApiResponses responses, CancellationToken token) =>
        {
            var result = await delivery.GetAsync(messageId, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/audit-deliveries/{messageId}", "GET")
            .Produces<ApiResponse<FactDeliveryState>>().ProducesApiErrors(404, 503);

        group.MapGet("/audit-deliveries/recovery-capacity", async (
            [FromServices] ICostingAuditDelivery delivery, ApiResponses responses, CancellationToken token) =>
        {
            var capacity = await delivery.ReadRecoveryCapacityAsync(token).ConfigureAwait(false);
            return capacity.IsSuccess ? (IResult)responses.Ok(capacity.Value) : Failure(capacity.Error);
        }).RequirePermission("/api/costing/audit-deliveries/recovery-capacity", "GET")
            .Produces<ApiResponse<FactDeliveryRecoveryCapacity>>().ProducesApiErrors(503);

        group.MapPost("/audit-deliveries/{messageId:guid}/retry", async (Guid messageId, RetryCostingAuditDeliveryRequest request,
            [FromServices] ICostingAuditDelivery delivery, ICurrentUser user, IClock clock, IExecutionContext execution,
            ApiResponses responses, CancellationToken token) =>
        {
            var recovered = await delivery.RecoverAsync(new(request.RequestId, messageId, request.ExpectedDeadLetteredAt,
                request.ExpectedRetryRevision, request.Reason), user.UserId ?? string.Empty, clock.UtcNow,
                execution.Capture(), token).ConfigureAwait(false);
            return recovered.IsSuccess ? (IResult)responses.Ok(recovered.Value) : Failure(recovered.Error);
        }).RequirePermission("/api/costing/audit-deliveries/{messageId}/retry", "POST")
            .Produces<ApiResponse<FactDeliveryRecoveryReceipt>>().ProducesApiErrors(409, 415, 503)
            .WithMetadata(new OperationDescription("costing.fact-delivery.recover", "恢复所属事实投递"));

        group.MapGet("/audit-deliveries/recoveries/{requestId:guid}", async (Guid requestId,
            [FromServices] ICostingAuditDelivery delivery, ApiResponses responses, CancellationToken token) =>
        {
            var receipt = await delivery.GetRecoveryAsync(requestId, token).ConfigureAwait(false);
            return receipt.IsSuccess ? (IResult)responses.Ok(receipt.Value) : Failure(receipt.Error);
        }).RequirePermission("/api/costing/audit-deliveries/recoveries/{requestId}", "GET")
            .Produces<ApiResponse<FactDeliveryRecoveryReceipt>>().ProducesApiErrors(404, 503);

        group.MapGet("/schedule-receipts/{occurrenceId:guid}", async (Guid occurrenceId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetScheduledCostReceipt(occurrenceId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/schedule-receipts/{occurrenceId}", "GET").Produces<ApiResponse<ScheduledCostReceipt>>();
        group.MapPost("/cost", async (UpdateCostInputs request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(request, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/cost", "POST").Produces<ApiResponse<CostCalculationStatus>>(202).ProducesApiErrors(415, 503)
            .WithMetadata(new OperationDescription("costing.cost.update", "更新成本组成并申请重算"));
        group.MapGet("/tasks/{taskId:guid}/delivery", async (Guid taskId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetCostDelivery(taskId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/tasks/{taskId}/delivery", "GET").Produces<ApiResponse<CostDeliveryStatus>>();
        group.MapPost("/tasks/{taskId:guid}/delivery/retry", async (Guid taskId, DeliveryRetryRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryCostDelivery(taskId, request.ExpectedDeadLetteredAt), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/tasks/{taskId}/delivery/retry", "POST").Produces<ApiResponse<CostDeliveryStatus>>(202).ProducesApiErrors(415);
        group.MapGet("/items/{itemId:guid}", async (Guid itemId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetCostSheet(itemId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/items/{itemId}", "GET").Produces<ApiResponse<CostSheetView>>();
        group.MapGet("/tasks", async (int? page, int? limit, string? state, Guid? itemId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var query = new ListCostCalculations(page ?? 1, limit ?? 50, state, itemId);
            var result = await sender.QueryAsync(query, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Page(result.Value.Items, result.Value.Total, new ApiPageRequest(query.Page, query.Limit)) : Failure(result.Error);
        }).RequirePermission("/api/costing/tasks", "GET").Produces<ApiPage<CostCalculationSummary>>();
        group.MapPost("/tasks/{taskId:guid}/cancel", async (Guid taskId, CancelRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new CancelCostingWork(taskId, request.ExpectedEpoch), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/tasks/{taskId}/cancel", "POST").Produces<ApiResponse<CostCalculationStatus>>().ProducesApiErrors(415)
            .WithMetadata(new OperationDescription("costing.task.cancel", "取消计算任务",
                new OperationSubjectRoute("CostCalculation", "taskId", OperationSubjectIdKind.Uuid)));
        group.MapGet("/tasks/{taskId:guid}", async (Guid taskId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetCostCalculation(taskId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/tasks/{taskId}", "GET").Produces<ApiResponse<CostCalculationStatus>>();
        group.MapPost("/tasks/{taskId:guid}/retry", async (Guid taskId, RetryRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryCostingWork(taskId, request.ExpectedEpoch), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/costing/tasks/{taskId}/retry", "POST").Produces<ApiResponse<CostCalculationStatus>>(202).ProducesApiErrors(415)
            .WithMetadata(new OperationDescription("costing.task.retry", "重试成本计算",
                new OperationSubjectRoute("CostCalculation", "taskId", OperationSubjectIdKind.Uuid)));
        return endpoints;
    }

    private static IResult Failure(Error error) => Results.Problem(title: error.Message,
        statusCode: error.Code switch
        {
            "costing.batch.not_found" or "costing.not_found" or "costing.delivery_not_found" or "costing.delivery_recovery.not_found" => StatusCodes.Status404NotFound,
            "costing.delivery_recovery.exhausted" or "costing.audit_policy.control_exhausted" or "costing.audit_capacity_exhausted" or "audit_capacity.unavailable" or "audit_capacity.busy" => StatusCodes.Status503ServiceUnavailable,
            "costing.batch.retry_conflict" or "costing.batch.cancel_conflict" or "costing.batch.request_conflict" or "costing.delivery_recovery.request_conflict" or "costing.audit_policy.conflict" or "costing.request_conflict" or "costing.version_conflict" or "costing.retry_conflict" or "costing.delivery_conflict" or "costing.cancel_conflict" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        }, extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

internal sealed record RetryRequest(long ExpectedEpoch);
internal sealed record CancelRequest([property: JsonRequired] long ExpectedEpoch);
internal sealed record CostingConnection(string Value);
internal sealed record DeliveryRetryRequest(DateTimeOffset ExpectedDeadLetteredAt);

internal sealed record RetryCostingAuditDeliveryRequest(Guid RequestId, DateTimeOffset ExpectedDeadLetteredAt, [property: JsonRequired] long ExpectedRetryRevision, string Reason);
