using System.Text.Json;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Application;

/// <summary>操作观察存储：同次提交登记消息身份、内容指纹与不可变阶段。</summary>
public interface IOperationObservationStore
{
    /// <summary>原子接纳阶段；同消息重投幂等，同阶段另一个消息拒绝。</summary>
    /// <param name="observation">已验证观察。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>首次、重复或冲突。</returns>
    Task<Result<IngestionOutcome>> AcceptAsync(OperationObservation observation, CancellationToken cancellationToken = default);

    /// <summary>按来源与执行标识汇总并有界查询。</summary>
    /// <param name="query">已验证过滤条件。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>执行汇总页。</returns>
    Task<OperationPage> QueryAsync(OperationQuery query, CancellationToken cancellationToken = default);

    /// <summary>分批删除所有阶段接收时间均早于截止时刻的操作，同时结束其消息去重对照。</summary>
    /// <param name="recordedBefore">UTC 接收时间的严格截止；不是来源发生时间。</param>
    /// <param name="maxOperations">本批最多处理的操作数，1 至 1000。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>本次提交删除的观察记录数；已提交事实不受影响。</returns>
    Task<int> DeleteExpiredAsync(DateTimeOffset recordedBefore, int maxOperations, CancellationToken cancellationToken = default);
}

/// <summary>校验操作观察契约并通过事务性存储接纳。</summary>
/// <param name="observations">观察存储。</param>
/// <param name="serializer">契约序列化。</param>
/// <param name="clock">中央接收时钟。</param>
public sealed class OperationObservationIngestion(IOperationObservationStore observations, IIntegrationEventSerializer serializer, IClock clock)
    : IIntegrationEventProcessor
{
    /// <summary>操作观察消费者身份。</summary>
    public const string ConsumerName = "auditing.operations";
    /// <summary>相同消息身份携带另一份内容。</summary>
    public static readonly Error MessageConflict = new("auditing.observation.message_conflict", "操作观察消息身份已对应不同内容。");
    /// <summary>相同来源、执行与阶段已有另一条不可变观察。</summary>
    public static readonly Error PhaseConflict = new("auditing.observation.phase_conflict", "操作阶段已经记录，不能覆盖或重复声明。");

    /// <inheritdoc />
    public string EventName => OperationObservedV1.Name;

    /// <summary>供来源 journal 与消费端共用的契约校验。</summary>
    /// <param name="message">安全观察契约。</param>
    /// <returns>有效或字段错误。</returns>
    public static Result Validate(OperationObservedV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var checkedObservation = Record(message, message.OccurredAt);
        return checkedObservation.IsSuccess ? Result.Success() : Result.Failure(checkedObservation.Error);
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        OperationObservedV1 message;
        try { message = serializer.Deserialize<OperationObservedV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId) { return false; }
        var observation = Record(message, clock.UtcNow);
        return observation.IsSuccess && (await observations.AcceptAsync(observation.Value, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }

    private static Result<OperationObservation> Record(OperationObservedV1 message, DateTimeOffset recordedAt) =>
        OperationObservation.Record(new OperationObservationId(message.EventId), new OperationObservationData(new OperationId(message.OperationId), message.Source, message.Kind,
            message.Phase, message.Outcome, message.OccurredAt, message.ActorId, message.TraceId, message.HttpMethod,
            message.RouteTemplate, message.StatusCode, message.DurationMs)
        {
            Metadata = message.Metadata is { } metadata ? new OperationMetadata(metadata.Action, metadata.ExecutionRole,
                metadata.Description, metadata.SubjectType, metadata.SubjectIdKind, metadata.SubjectId,
                metadata.SpanId, metadata.ParentSpanId, metadata.CorrelationId)
            {
                RootOperationId = metadata.RootOperationId,
                RootSource = metadata.RootSource,
                ParentOperationId = metadata.ParentOperationId,
                ParentSource = metadata.ParentSource,
                InitiatorId = metadata.InitiatorId,
                TaskId = metadata.TaskId,
                TaskEpoch = metadata.TaskEpoch,
                SchedulePlanId = metadata.SchedulePlanId,
                ScheduleExpectedVersion = metadata.ScheduleExpectedVersion,
                ScheduleDecisionId = metadata.ScheduleDecisionId,
            } : null,
        }, recordedAt);
}
