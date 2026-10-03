namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>所属上下文已交付审计事实副本的有界清理，不删除中央事实、业务消息或 Inbox。</summary>
public interface ICommittedFactCleanup
{
    /// <summary>清理一批超过保留期的已确认副本；待投递和死信保留。</summary>
    /// <param name="cancellationToken">本轮维护预算。</param>
    /// <returns>本轮实际提交删除的条数。</returns>
    Task<int> CleanupAsync(CancellationToken cancellationToken = default);
}
