using Microsoft.AspNetCore.SignalR;

namespace NexusStackNext.Gateway;

/// <summary>
/// 后端可达性**变化时**向已连接的客户端推送。
///
/// <para><b>只在变化时推</b>，不是每个周期都推。理由与聚合版本号的"空操作不自增"是同一条：
/// 一个不断重复同一内容的通道会让客户端学会忽略它，于是它真正要传达的那一次也会被忽略。</para>
///
/// <para>这也是"边缘是一个服务"的体现：客户端不必自己轮询各后端的健康，
/// 边缘已经知道——而它恰好是唯一知道全貌的地方。</para>
/// </summary>
/// <param name="probe">可达性探测。</param>
/// <param name="hubContext">推送通道。</param>
/// <param name="logger">日志。</param>
public sealed partial class ClusterStatusBroadcaster(
    ClusterReachabilityProbe probe,
    IHubContext<GatewayHub> hubContext,
    ILogger<ClusterStatusBroadcaster> logger) : BackgroundService
{
    /// <summary>探测周期。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动时先推一次当前状态，客户端一连上就知道现状，不必等第一次变化。
        ClusterReachability? last = null;

        using var timer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var current = await probe.CheckAsync(stoppingToken).ConfigureAwait(false);

                if (HasChanged(last, current))
                {
                    last = current;

                    await hubContext.Clients.All
                        .SendAsync(
                            GatewayHub.ClusterStatusMessage,
                            new
                            {
                                healthy = current.IsHealthy,
                                clusterCount = current.ClusterCount,
                                unreachable = current.Unreachable,
                            },
                            stoppingToken)
                        .ConfigureAwait(false);

                    LogStatusPushed(logger, current.IsHealthy, current.ClusterCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 广播失败不能让后台服务死掉——它死了就再也没人推了，而没人会注意到。
                LogBroadcastFailed(logger, exception);
            }

            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private static bool HasChanged(ClusterReachability? previous, ClusterReachability current)
    {
        if (previous is null)
        {
            return true;
        }

        return previous.IsHealthy != current.IsHealthy
            || previous.ClusterCount != current.ClusterCount
            || !previous.Unreachable.SequenceEqual(current.Unreachable, StringComparer.Ordinal);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "已推送后端状态：{Healthy}（{ClusterCount} 个 cluster）")]
    private static partial void LogStatusPushed(ILogger logger, bool healthy, int clusterCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "后端状态广播失败。")]
    private static partial void LogBroadcastFailed(ILogger logger, Exception exception);
}
