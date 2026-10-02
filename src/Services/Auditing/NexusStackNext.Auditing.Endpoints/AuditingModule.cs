using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Platform.Contracts;

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
        var broker = configuration.GetSection("RabbitMQ").Get<RabbitMqOptions>();
        if (broker is not null && !string.IsNullOrWhiteSpace(broker.HostName) && configuration.GetValue("Auditing:Messaging:Enabled", true))
        {
            broker.Validate();
            var consumer = configuration.GetValue<string>("Auditing:Messaging:ConsumerName") ?? AuditIngestion.ConsumerName;
            ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
            services.AddNexusStackRabbitMqConsumer(broker, new EventSubscription { EventName = SettingCommittedV1.Name, ConsumerName = consumer });
            services.AddHealthChecks().AddAsyncCheck("auditing-broker", async token =>
                await RabbitMqReadiness.IsReadyAsync(broker, token).ConfigureAwait(false)
                    ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("审计消息 broker 不可用。"));
        }
        var provider = configuration["Auditing:Storage:Provider"];
        if (string.IsNullOrWhiteSpace(provider)) { provider = "Postgres"; }
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Auditing:Storage:Provider=Memory 仅允许开发测试使用。");
            }
            return services.AddAuditingInMemoryStorage();
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
        return services.AddAuditingPostgresStorage(connection);
    }

    /// <summary>只公开调查查询；审计事实不接受 HTTP 写入。</summary>
    /// <param name="endpoints">路由。</param>
    /// <returns>路由。</returns>
    public static IEndpointRouteBuilder MapAuditingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/auditing").RequireAuthorization()
            .ProducesApiErrors(400, 401, 403, 500);
        group.AddEndpointFilter<NexusStackAuthorizationFilter>();
        group.MapGet("/entries", async (IAuditEntryStore entries, ApiResponses responses,
            [AsParameters] ApiPageRequest paging, CancellationToken cancellationToken) =>
        {
            if (paging.Page is < 1 or > 1000 || paging.Limit is < 1 or > 100)
            {
                return Results.Problem(title: "审计查询 page 必须在 1 到 1000，limit 必须在 1 到 100。",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "auditing.pagination.invalid" });
            }
            var found = await entries.QueryAsync(paging.Page, paging.Limit, cancellationToken).ConfigureAwait(false);
            return (IResult)responses.Page(found.Entries.Select(static entry => new AuditEntryResponse(
                entry.Id.Value, entry.Fact, entry.RecordedAt)).ToArray(), found.Total, paging);
        }).RequirePermission("/api/auditing/entries", "GET").Produces<ApiPage<AuditEntryResponse>>();
        return endpoints;
    }
}

internal sealed record AuditEntryResponse(long Id, NexusStackNext.Auditing.Domain.Entries.AuditFact Fact, DateTimeOffset RecordedAt);
