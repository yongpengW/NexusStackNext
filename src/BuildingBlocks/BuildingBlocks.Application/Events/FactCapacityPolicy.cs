using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>所属存储原子管理策略、请求凭据与控制事实，不借用业务工作作用域。</summary>
public interface ICommittedFactCapacityPolicyStore
{
    /// <summary>读取策略和两种额度的同一已提交快照。</summary>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>所属账本快照或明确不可用。</returns>
    Task<Result<FactCapacityPolicySnapshot>> ReadPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>条件调整；凭据重放先于版本比较，失败不发布部分状态。</summary>
    /// <param name="request">有界条件请求。</param>
    /// <param name="actorId">已经认证与授权的操作者。</param>
    /// <param name="occurredAt">注入时钟给出的发生时刻。</param>
    /// <param name="execution">可信执行关联，不用于授权。</param>
    /// <param name="cancellationToken">实际发布之前可取消。</param>
    /// <returns>本次或先前已提交的裁决。</returns>
    Task<Result<FactCapacityPolicyReceipt>> AdjustAsync(FactCapacityPolicyRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default);
}

/// <summary>固定理由的条件策略请求；所有长整数沿用 HTTP 十进制字符串契约。</summary>
/// <param name="RequestId">调用方请求标识，不能直接用作事件身份。</param>
/// <param name="ExpectedPolicyRevision">期望的策略版本。</param>
/// <param name="MaxRecords">新条数上限。</param>
/// <param name="MaxPayloadBytes">新总载荷上限。</param>
/// <param name="MaxRecordPayloadBytes">新单条上限。</param>
/// <param name="Reason">只接受 operator-adjustment。</param>
public sealed record FactCapacityPolicyRequest(Guid RequestId, long ExpectedPolicyRevision, long MaxRecords,
    long MaxPayloadBytes, int MaxRecordPayloadBytes, string Reason)
{
    /// <summary>拒绝空身份、非正数、不一致额度和任意理由文本。</summary>
    [JsonIgnore]
    public bool IsValid => RequestId != Guid.Empty && ExpectedPolicyRevision > 0 && MaxRecords > 0
        && MaxPayloadBytes > 0 && MaxRecordPayloadBytes > 0 && MaxRecordPayloadBytes <= MaxPayloadBytes
        && Reason == "operator-adjustment";

    /// <summary>请求期望的三个额度。</summary>
    [JsonIgnore]
    public FactCapacityPolicyLimits Limits => new(MaxRecords, MaxPayloadBytes, MaxRecordPayloadBytes);
}

/// <summary>过去的策略裁决，不承诺它仍是当前策略。</summary>
/// <param name="RequestId">原请求身份。</param>
/// <param name="PolicyRevision">裁决完成时的版本。</param>
/// <param name="Changed">是否确实修改策略。</param>
/// <param name="Previous">裁决前额度。</param>
/// <param name="Current">裁决后额度。</param>
/// <param name="EventId">实际变化的事件身份；空操作没有事件。</param>
/// <param name="AcceptedAt">接受时刻。</param>
/// <param name="RetainUntil">固定最早凭据保留时间。</param>
public sealed record FactCapacityPolicyReceipt(Guid RequestId, long PolicyRevision, bool Changed,
    FactCapacityPolicyLimits Previous, FactCapacityPolicyLimits Current, Guid? EventId,
    DateTimeOffset AcceptedAt, DateTimeOffset RetainUntil);

/// <summary>业务容量与独立控制容量在同一存储锁下的诊断。</summary>
/// <param name="Business">已提交业务容量。</param>
/// <param name="PolicyRevision">策略的单调版本。</param>
/// <param name="ControlCapacity">有限控制额度。</param>
public sealed record FactCapacityPolicySnapshot([property: JsonIgnore] CommittedFactCapacitySnapshot Business, long PolicyRevision,
    FactCapacityControlSnapshot ControlCapacity)
{
    /// <summary>所属上下文。</summary>
    public string Context => Business.Context;
    /// <summary>是否持久。</summary>
    public bool IsPersistent => Business.IsPersistent;
    /// <summary>业务条数上限。</summary>
    public long MaxRecords => Business.MaxRecords;
    /// <summary>业务总载荷上限。</summary>
    public long MaxPayloadBytes => Business.MaxPayloadBytes;
    /// <summary>业务单条上限。</summary>
    public int MaxRecordPayloadBytes => Business.MaxRecordPayloadBytes;
    /// <summary>业务保留条数。</summary>
    public long RetainedRecords => Business.RetainedRecords;
    /// <summary>业务保留载荷。</summary>
    public long RetainedPayloadBytes => Business.RetainedPayloadBytes;
    /// <summary>业务剩余条数。</summary>
    public long RemainingRecords => Business.RemainingRecords;
    /// <summary>业务剩余载荷。</summary>
    public long RemainingPayloadBytes => Business.RemainingPayloadBytes;
    /// <summary>业务是否超限。</summary>
    public bool OverLimit => Business.OverLimit;
}

/// <summary>控制请求及其事实的有限额度，每个请求计一次。</summary>
/// <param name="MaxRecords">请求上限。</param>
/// <param name="MaxPayloadBytes">凭据和事实的 UTF-8 总字节上限。</param>
/// <param name="MaxRecordPayloadBytes">单个请求的凭据与事实字节上限。</param>
/// <param name="RetainedRecords">已保留请求数。</param>
/// <param name="RetainedPayloadBytes">已保留凭据和事实字节数。</param>
public sealed record FactCapacityControlSnapshot(long MaxRecords, long MaxPayloadBytes, int MaxRecordPayloadBytes,
    long RetainedRecords, long RetainedPayloadBytes);
