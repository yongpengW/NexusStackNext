using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tasks;

/// <summary>上下文数据库内的执行元数据；不是业务聚合或跨上下文任务仓库。</summary>
public abstract class DurableTaskRecord
{
    /// <summary>由调用方提供的稳定工作标识。</summary>
    public Guid TaskId { get; set; }
    /// <summary>受理时保存的来源；同一业务请求重放不覆盖，旧任务可能为空。</summary>
    public ExecutionOrigin? ExecutionOrigin { get; set; }
    /// <summary>当前执行状态。</summary>
    public string State { get; set; } = "Pending";
    /// <summary>数据库首次接受时刻；升级前任务没有可靠历史值。</summary>
    public DateTimeOffset? CreatedAt { get; set; }
    /// <summary>最早领取时刻。</summary>
    public DateTimeOffset AvailableAt { get; set; }
    /// <summary>当前租约期限。</summary>
    public DateTimeOffset? LeaseUntil { get; set; }
    /// <summary>本次领取的固定总期限；升级前的旧领取为未知，不能续租。</summary>
    public DateTimeOffset? MaxLeaseUntil { get; set; }
    /// <summary>从不回退的执行代次。</summary>
    public long Epoch { get; set; }
    /// <summary>本轮预算内的尝试次数。</summary>
    public int Attempts { get; set; }
    /// <summary>稳定错误码。</summary>
    public string? ErrorCode { get; set; }
    /// <summary>全部执行历史，人工重试不清空。</summary>
    public List<DurableTaskAttempt> History { get; set; } = [];
}

/// <summary>某次领取与结束的持久化记录。</summary>
public sealed class DurableTaskAttempt
{
    /// <summary>工作标识。</summary>
    public Guid TaskId { get; set; }
    /// <summary>领取代次。</summary>
    public long Epoch { get; set; }
    /// <summary>数据库给出的开始时间。</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>结束时间。</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>执行结论。</summary>
    public string Outcome { get; set; } = "Running";
    /// <summary>稳定错误码。</summary>
    public string? ErrorCode { get; set; }
}
