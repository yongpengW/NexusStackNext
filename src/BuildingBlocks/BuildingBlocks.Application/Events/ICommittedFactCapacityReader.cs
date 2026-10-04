using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>读取所属上下文事实账本的单一快照；不读取事实正文或修改策略。</summary>
public interface ICommittedFactCapacityReader
{
    /// <summary>有界读取；不可用返回明确失败，调用者取消继续传播。</summary>
    /// <param name="cancellationToken">调用者的取消信号。</param>
    /// <returns>已提交占用与策略的同一快照，不可用时不伪造零占用。</returns>
    Task<Result<CommittedFactCapacitySnapshot>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>容量访问的稳定错误；写入容量耗尽仍使用所属业务模块的原错误。</summary>
public static class CommittedFactCapacityErrors
{
    /// <summary>账本暂不可读、缺失或读取预算用尽。</summary>
    public static readonly Error Unavailable = new("audit_capacity.unavailable", "事实容量暂时不可查询。");

    /// <summary>容量账本争用超时；不代表额度已经耗尽。</summary>
    public static readonly Error Busy = new("audit_capacity.busy", "审计事实容量账本繁忙，本次变更未提交，请稍后重试。");
}
