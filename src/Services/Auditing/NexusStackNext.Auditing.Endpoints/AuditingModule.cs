using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.Auditing.Endpoints;

/// <summary>审计模块：消息摄取与受权限保护的调查查询。</summary>
public static class AuditingModule
{
    /// <summary>注册审计存储。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">审计配置。</param>
    /// <param name="environment">运行环境。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddAuditingModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        services.AddKeyedScoped<IIntegrationEventProcessor, PlatformAuditIngestion>(SettingCommittedV1.Name);
        AddPolicyIngestion<SettingFactCapacityPolicyChangedV1>(services, "platform", SettingFactCapacityPolicyChangedV1.Name);
        AddPolicyIngestion<IdentityFactCapacityPolicyChangedV1>(services, "identity", IdentityFactCapacityPolicyChangedV1.Name);
        AddPolicyIngestion<FilesFactCapacityPolicyChangedV1>(services, "files", FilesFactCapacityPolicyChangedV1.Name);
        AddPolicyIngestion<SchedulingFactCapacityPolicyChangedV1>(services, "scheduling", SchedulingFactCapacityPolicyChangedV1.Name);
        AddPolicyIngestion<CostingFactCapacityPolicyChangedV1>(services, "costing", CostingFactCapacityPolicyChangedV1.Name);
        AddPolicyIngestion<PricingFactCapacityPolicyChangedV1>(services, "pricing", PricingFactCapacityPolicyChangedV1.Name);
        services.AddKeyedScoped<IIntegrationEventProcessor, IdentityAuditIngestion>(IdentityEntityCommittedV1.Name);
        services.AddKeyedScoped<IIntegrationEventProcessor, FilesAuditIngestion>(StoredFileCommittedV1.Name);
        services.AddKeyedScoped<IIntegrationEventProcessor, SchedulingAuditIngestion>(PlanCommittedV1.Name);
        services.AddKeyedScoped<IIntegrationEventProcessor, CostingAuditIngestion>(CostSheetCommittedV1.Name);
        services.AddKeyedScoped<IIntegrationEventProcessor, PricingAuditIngestion>(PriceQuoteCommittedV1.Name);
        services.AddKeyedScoped<IIntegrationEventProcessor, OperationObservationIngestion>(OperationObservedV1.Name);
        var broker = configuration.GetSection("RabbitMQ").Get<RabbitMqOptions>();
        if (broker is not null && !string.IsNullOrWhiteSpace(broker.HostName) && configuration.GetValue("Auditing:Messaging:Enabled", true))
        {
            broker.Validate();
            var consumer = configuration.GetValue<string>("Auditing:Messaging:ConsumerName") ?? AuditIngestion.ConsumerName;
            ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = SettingFactCapacityPolicyChangedV1.Name, ConsumerName = consumer });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = IdentityFactCapacityPolicyChangedV1.Name, ConsumerName = consumer });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = FilesFactCapacityPolicyChangedV1.Name, ConsumerName = consumer });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = SchedulingFactCapacityPolicyChangedV1.Name, ConsumerName = consumer });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = CostingFactCapacityPolicyChangedV1.Name, ConsumerName = consumer });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = PricingFactCapacityPolicyChangedV1.Name, ConsumerName = consumer });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = SettingCommittedV1.Name, ConsumerName = consumer });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = IdentityEntityCommittedV1.Name, ConsumerName = consumer + "-identity" });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = StoredFileCommittedV1.Name, ConsumerName = consumer + "-files" });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = PlanCommittedV1.Name, ConsumerName = consumer + "-scheduling" });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = CostSheetCommittedV1.Name, ConsumerName = consumer + "-costing" });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = PriceQuoteCommittedV1.Name, ConsumerName = consumer + "-pricing" });
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription
            {
                EventName = OperationObservedV1.Name,
                ConsumerName = configuration.GetValue<string>("Auditing:Messaging:OperationConsumerName") ?? OperationObservationIngestion.ConsumerName,
            });
            services.AddHealthChecks().AddAsyncCheck("auditing-broker", async token =>
                await RabbitMqReadiness.IsReadyAsync(broker, token).ConfigureAwait(false)
                    ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("审计消息 broker 不可用。"), tags: [AuditingDiagnostics.HealthTag]);
        }
        var capacity = configuration.GetSection("Auditing:Capacity").Get<AuditStorageCapacityOptions>() ?? new();
        services.AddHealthChecks().AddCheck<AuditingCapacityHealthCheck>("auditing-capacity", tags: [AuditingDiagnostics.HealthTag]);
        var provider = configuration["Auditing:Storage:Provider"];
        if (string.IsNullOrWhiteSpace(provider)) { provider = "Postgres"; }
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Auditing:Storage:Provider=Memory 仅允许开发测试使用。");
            }
            return services.AddAuditingInMemoryStorage(capacity);
        }
        if (!string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Auditing:Storage:Provider 仅支持 Postgres / Memory。");
        }
        var connection = configuration.GetConnectionString("Auditing");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("必须配置 ConnectionStrings:Auditing；开发测试可显式选择 Auditing:Storage:Provider=Memory。");
        }
        return services.AddAuditingPostgresStorage(connection, capacity);
    }

    private static void AddPolicyIngestion<TEvent>(IServiceCollection services, string source, string eventName) where TEvent : FactCapacityPolicyChanged =>
        services.AddKeyedScoped<IIntegrationEventProcessor>(eventName, (provider, _) => new FactCapacityPolicyAuditIngestion<TEvent>(
            provider.GetRequiredService<AuditIngestion>(), provider.GetRequiredService<IIntegrationEventSerializer>(), source, eventName));

    /// <summary>只公开调查查询；审计事实不接受 HTTP 写入。</summary>
    /// <param name="endpoints">路由。</param>
    /// <returns>路由。</returns>
    public static IEndpointRouteBuilder MapAuditingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/auditing").RequireAuthorization()
            .ProducesApiErrors(400, 401, 403, 500);
        group.AddEndpointFilter<NexusStackAuthorizationFilter>();
        group.MapGet("/capacity", async (IAuditStorageCapacityReader capacity, ApiResponses responses, CancellationToken token) =>
        {
            try { return (IResult)responses.Ok(await capacity.ReadAsync(token).ConfigureAwait(false)); }
            catch (AuditStorageUnavailableException)
            {
                return Results.Problem(title: "中央审计容量诊断暂不可用。", statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "auditing.storage_unavailable" });
            }
        }).RequirePermission("/api/auditing/capacity", "GET").Produces<ApiResponse<AuditStorageCapacitySnapshot>>().ProducesApiErrors(503)
            .WithMetadata(new OperationLogSuppression("中央容量调查不产生新的操作观察，避免诊断放大日志。"));
        group.MapGet("/entries", async (IAuditEntryStore entries, ApiResponses responses, IClock clock,
            [AsParameters] ApiPageRequest paging, string? source, string? action, string? subjectType, string? subjectId,
            string? relatedContext, string? relatedSubjectType, string? relatedSubjectId,
            string? actorId, string? traceId, string? correlationId, Guid? operationId, string? operationSource,
            Guid? rootOperationId, string? rootSource, string? initiatorId, DateTimeOffset? from, DateTimeOffset? to,
            CancellationToken cancellationToken) =>
        {
            var query = new AuditQuery(paging.Page, paging.Limit)
            {
                Source = source,
                Action = action,
                SubjectType = subjectType,
                SubjectId = subjectId,
                RelatedContext = relatedContext,
                RelatedSubjectType = relatedSubjectType,
                RelatedSubjectId = relatedSubjectId,
                ActorId = actorId,
                TraceId = traceId,
                CorrelationId = correlationId,
                OperationId = operationId,
                OperationSource = operationSource,
                RootOperationId = rootOperationId,
                RootSource = rootSource,
                InitiatorId = initiatorId,
                From = from,
                To = to,
            }.Normalize(clock.UtcNow);
            var validation = query.Validate();
            if (validation.IsFailure)
            {
                return Results.Problem(title: validation.Error.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = validation.Error.Code });
            }
            var found = await entries.QueryAsync(query, cancellationToken).ConfigureAwait(false);
            return (IResult)responses.Page(found.Entries.Select(static entry => new AuditEntryResponse(
                entry.Id.Value, entry.Fact, entry.RecordedAt)).ToArray(), found.Total, paging);
        }).RequirePermission("/api/auditing/entries", "GET").Produces<ApiPage<AuditEntryResponse>>()
            .WithMetadata(new OperationLogSuppression("调查查询不产生新的操作观察，避免查询放大日志。"));
        group.MapGet("/operations", async (IOperationObservationStore observations, ApiResponses responses, IClock clock,
            [AsParameters] ApiPageRequest paging, string? source, Guid? operationId, string? outcome, string? actorId,
            string? traceId, DateTimeOffset? from, DateTimeOffset? to, string? action, string? subjectType, string? subjectId,
            string? correlationId, string? initiatorId, Guid? rootOperationId, string? rootSource,
            Guid? parentOperationId, string? parentSource, Guid? taskId, long? taskEpoch, CancellationToken cancellationToken) =>
        {
            var query = new OperationQuery(paging.Page, paging.Limit, source, operationId, outcome, actorId, traceId, from, to)
            {
                Action = action,
                SubjectType = subjectType,
                SubjectId = subjectId,
                CorrelationId = correlationId,
                InitiatorId = initiatorId,
                RootOperationId = rootOperationId,
                RootSource = rootSource,
                ParentOperationId = parentOperationId,
                ParentSource = parentSource,
                TaskId = taskId,
                TaskEpoch = taskEpoch,
            }.Normalize(clock.UtcNow);
            var validation = query.Validate();
            if (validation.IsFailure)
            {
                return Results.Problem(title: validation.Error.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = validation.Error.Code });
            }
            var found = await observations.QueryAsync(query, cancellationToken).ConfigureAwait(false);
            return (IResult)responses.Page(found.Operations, found.Total, paging);
        }).RequirePermission("/api/auditing/operations", "GET").Produces<ApiPage<OperationSummary>>()
            .WithMetadata(new OperationLogSuppression("调查查询不产生新的操作观察，避免查询放大日志。"));
        return endpoints;
    }
}

internal sealed record AuditEntryResponse(long Id, NexusStackNext.Auditing.Domain.Entries.AuditFact Fact, DateTimeOffset RecordedAt);
