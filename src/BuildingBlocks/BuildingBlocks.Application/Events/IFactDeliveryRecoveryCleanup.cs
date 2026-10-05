namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>所属恢复凭据的有限维护端口，不修改或重开原消息。</summary>
public interface IFactDeliveryRecoveryCleanup
{
    /// <summary>按固定期限和稳定顺序原子释放有限批次凭据及其原计量。</summary>
    /// <param name="batchSize">每轮一至一千个请求。</param>
    /// <param name="now">维护时钟给出的当前时刻。</param>
    /// <param name="cancellationToken">实际发布前可取消。</param>
    /// <returns>实际释放的恢复请求数。</returns>
    Task<int> CleanupRecoveriesAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default);
}
