namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>所属策略凭据和确认控制事实的有界维护，不改变业务额度或策略版本。</summary>
public interface ICommittedFactCapacityPolicyCleanup
{
    /// <summary>按固定最早保留期和实际交付期原子清理；待投递和死信保持。</summary>
    /// <param name="batchSize">本轮最多释放的请求数，允许一至一千。</param>
    /// <param name="now">维护宿主注入时钟给出的当前时间。</param>
    /// <param name="cancellationToken">本轮有限维护预算。</param>
    /// <returns>实际原子释放的请求数，空操作凭据也计一次。</returns>
    Task<int> CleanupAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default);
}
