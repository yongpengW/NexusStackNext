using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Domain.Operations;

/// <summary>一次执行某阶段的不可变观察，不含业务载荷或异常原文。</summary>
/// <param name="OperationId">来源生成的执行标识。</param>
/// <param name="Source">可信来源。</param>
/// <param name="Kind">执行入口。</param>
/// <param name="Phase">开始或完成。</param>
/// <param name="Outcome">完成结果；开始为空。</param>
/// <param name="OccurredAt">来源观察时刻。</param>
/// <param name="ActorId">来源认证的执行者。</param>
/// <param name="TraceId">来源追踪标识。</param>
/// <param name="HttpMethod">HTTP 方法。</param>
/// <param name="RouteTemplate">路由模板；未匹配为空。</param>
/// <param name="StatusCode">实际观察到的状态码。</param>
/// <param name="DurationMs">单调计时耗时。</param>
public sealed record OperationObservationData(OperationId OperationId, string Source, string Kind, string Phase, string? Outcome,
    DateTimeOffset OccurredAt, string? ActorId, string TraceId, string? HttpMethod, string? RouteTemplate, int? StatusCode, long? DurationMs);

/// <summary>不可修改的一条操作观察；消息身份由来源提供。</summary>
public sealed class OperationObservation : Entity<OperationObservationId>
{
    private OperationObservation(OperationObservationId id) : base(id) { Data = null!; }

    /// <summary>来源观察。</summary>
    public OperationObservationData Data { get; private init; }
    /// <summary>中央接收时刻；与来源发生时刻不同。</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>验证安全字段和阶段语义后创建不可变记录。</summary>
    /// <param name="messageId">稳定消息标识。</param>
    /// <param name="data">来源观察。</param>
    /// <param name="recordedAt">中央接收时刻。</param>
    /// <returns>已验证的观察或错误。</returns>
    public static Result<OperationObservation> Record(OperationObservationId messageId, OperationObservationData data, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (messageId.Value == Guid.Empty || data.OperationId.Value == Guid.Empty || data.OccurredAt == default || data.OccurredAt.Offset != TimeSpan.Zero
            || recordedAt == default || recordedAt.Offset != TimeSpan.Zero || !Safe(data.Source, 64) || !Safe(data.TraceId, 128)
            || (data.ActorId is not null && !Safe(data.ActorId, 200)) || data.Kind != "http"
            || !Safe(data.HttpMethod, 16) || !data.HttpMethod!.All(char.IsAsciiLetter)
            || (data.RouteTemplate is not null && (!Safe(data.RouteTemplate, 500) || !data.RouteTemplate.StartsWith('/')))
            || !ValidPhase(data))
        {
            return Result.Failure<OperationObservation>(new Error("auditing.observation.invalid", "操作观察包含无效的身份、安全字段或阶段结果。"));
        }
        return Result.Success(new OperationObservation(messageId) { Data = data, RecordedAt = recordedAt });
    }

    private static bool ValidPhase(OperationObservationData data) => data.Phase switch
    {
        "started" => data.Outcome is null && data.StatusCode is null && data.DurationMs is null,
        "finished" => data.DurationMs is >= 0 && (data.Outcome switch
        {
            "accepted" => data.StatusCode == 202,
            "completed" => data.StatusCode is >= 200 and < 400 and not 202,
            "rejected" => data.StatusCode is >= 400 and < 500,
            "failed" => data.StatusCode is null or >= 500 and <= 599,
            "canceled" => data.StatusCode is null or >= 100 and <= 599,
            _ => false,
        }),
        _ => false,
    };

    private static bool Safe(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
}
