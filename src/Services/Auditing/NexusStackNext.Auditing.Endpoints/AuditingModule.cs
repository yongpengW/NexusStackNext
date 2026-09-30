using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.Auditing.Endpoints;

/// <summary>
/// Auditing 模块：本上下文对宿主暴露的**全部**内容——DI 注册与 HTTP 端点。
///
/// <para><b>为什么有这一层。</b>平台能力（Identity / Platform / Scheduling / Auditing / Files）
/// 是通用子域：它们服务于业务，但本身不是业务差异化所在，而且通常一起演进、一起部署。
/// 因此它们由<b>一个</b>宿主组装，而不是各自一个进程（ADR-0002 的修订、ADR-0013）。</para>
///
/// <para><b>建模边界没有因此变松。</b>这五个上下文仍然是各自独立的
/// <c>Domain</c> / <c>Application</c> / <c>Infrastructure</c> 程序集，仍然零跨上下文引用，
/// 架构不变量测试一条没改。<b>变的只是"跑在几个进程里"。</b></para>
///
/// <para>宿主仍然显式组装自己（不变量 8）：它逐行列出一个模块，
/// 而不是靠一个 <c>moduleKey</c> 在运行时决定自己是谁。</para>
/// </summary>
public static class AuditingModule
{
    /// <summary>注册本模块需要的服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合，便于串联。</returns>
    public static IServiceCollection AddAuditingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuditingInMemoryStorage();
        return services;
    }

    /// <summary>
    /// 映射本模块的端点。
    ///
    /// <para>Auditing 是**只写**上下文（ADR-0001）：审计条目由事件进入，
    /// 没有对外的业务查询端点。访问 <c>GET /api/auditing/entries</c> 得到 404 是故意的。</para>
    /// </summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static IEndpointRouteBuilder MapAuditingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/auditing", (IClock clock) => Results.Ok(new
        {
            context = "auditing",
            responsibility = "谁在什么时候改了什么",
            writeOnly = true,
            queryEndpoints = false,
            note = "只写上下文：没有业务查询端点，这是刻意的（ADR-0001）",
            at = clock.UtcNow,
        }));

        // 事件进入的端口。
        //
        // **生产里事件从消息总线来，不是从这个端点来。** 它存在是因为消息基座需要一个
        // 无需 broker 就能被端到端跑起来的入口——而"消费端幂等"这条保证只有在真的收到
        // 第二条消息时才验证得了。总线接入之后，这个端点的定位应当重新评估。
        endpoints.MapPost("/api/auditing/entries", async (
            IngestAuditRequest request,
            AuditIngestion ingestion,
            CancellationToken cancellationToken) =>
        {
            var result = await ingestion.IngestAsync(
                new AuditIngestedMessage(
                    request.MessageId,
                    request.EventName ?? "audit.recorded.v1",
                    request.Action,
                    request.SubjectType,
                    request.SubjectId,
                    request.ActorId,
                    request.Detail),
                cancellationToken);

            if (result.IsFailure)
            {
                return Results.Problem(
                    title: result.Error.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
            }

            // 202 = 记下了；200 + Duplicate = 这条我收过，**没有**再记一次。
            // 两者的区别就是这个端点存在的意义：它让"消费端幂等"成为可观察的事实。
            return result.Value == IngestionOutcome.Accepted
                ? Results.Json(new { outcome = "Accepted" }, statusCode: StatusCodes.Status202Accepted)
                : Results.Ok(new { outcome = "Duplicate" });
        });

        return endpoints;
    }
}

/// <summary>投递一条审计事件。</summary>
/// <param name="MessageId">消息标识，<b>幂等的依据</b>——重投同一条消息必须携带同一个值。</param>
/// <param name="EventName">事件名（含版本）；缺省为 <c>audit.recorded.v1</c>。</param>
/// <param name="Action">动作。</param>
/// <param name="SubjectType">客体类型。</param>
/// <param name="SubjectId">客体标识。</param>
/// <param name="ActorId">操作者。</param>
/// <param name="Detail">细节。</param>
internal sealed record IngestAuditRequest(
    Guid MessageId,
    string? EventName,
    string Action,
    string SubjectType,
    string SubjectId,
    string? ActorId,
    string? Detail);
