namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>共用事实写锁的获取预算用尽；用于没有 Result 返回值的存储与维护端口。</summary>
public sealed class CommittedFactCapacityBusyException() : Exception(CommittedFactCapacityErrors.Busy.Message)
{
    /// <summary>稳定的暂时争用错误；不表示额度耗尽或业务冲突。</summary>
    public static NexusStackNext.BuildingBlocks.Domain.Error Reason => CommittedFactCapacityErrors.Busy;
}
