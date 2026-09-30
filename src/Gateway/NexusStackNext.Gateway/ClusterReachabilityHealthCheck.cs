using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.Gateway;

/// <summary>一次后端可达性探测的结果。</summary>
/// <param name="ClusterCount">被探测的 cluster 总数。</param>
/// <param name="Unreachable">不可达的 cluster 描述；空表示全部可达。</param>
public sealed record ClusterReachability(int ClusterCount, IReadOnlyList<string> Unreachable)
{
    /// <summary>是否全部可达。</summary>
    public bool IsHealthy => Unreachable.Count == 0;
}

/// <summary>
/// 探测每个 cluster 是否至少有一个可达目标。
///
/// <para><b>两个消费者共用它</b>：就绪检查（<c>/health/ready</c>）与状态广播（SignalR）。
/// 一个消费者时它只是一段代码；两个之后，"探测"这件事才成为一道真的缝——
/// 这也是把 SAME 逻辑抽出来的理由，而不是"看起来更整洁"。</para>
///
/// <para>探的是目标的 <c>/health/live</c>（存活），不是 <c>/health/ready</c>——
/// 后者会让两个就绪状态互相依赖。上游只想知道"这个目标还接不接得上"。</para>
/// </summary>
/// <param name="httpClientFactory">HTTP 客户端工厂。</param>
/// <param name="routeTable">当前生效的路由表。</param>
public sealed class ClusterReachabilityProbe(
    IHttpClientFactory httpClientFactory,
    GatewayRouteTable routeTable)
{
    /// <summary>单个目标的探测超时。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>探测所有 cluster。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结果。</returns>
    public async Task<ClusterReachability> CheckAsync(CancellationToken cancellationToken = default)
    {
        var unreachable = new List<string>();

        foreach (var cluster in routeTable.Clusters)
        {
            if (cluster.Destinations.Count == 0)
            {
                unreachable.Add($"{cluster.ClusterId}（没有目标）");
                continue;
            }

            // 一个目标可达就算这个 cluster 可用——多副本时挂一个不该让整个集群判死。
            var reachable = false;
            foreach (var destination in cluster.Destinations)
            {
                if (await IsReachableAsync(destination.Address, cancellationToken).ConfigureAwait(false))
                {
                    reachable = true;
                    break;
                }
            }

            if (!reachable)
            {
                unreachable.Add($"{cluster.ClusterId}（{cluster.Destinations.Count} 个目标都不可达）");
            }
        }

        return new ClusterReachability(routeTable.Clusters.Count, unreachable);
    }

    private async Task<bool> IsReachableAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(ClusterReachabilityProbe));
            client.Timeout = ProbeTimeout;

            using var response = await client
                .GetAsync($"{address.TrimEnd('/')}/health/live", cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// 网关的就绪检查：**每个 cluster 至少要有一个可达的目标。**
///
/// <para><b>为什么这条重要。</b>网关的 <c>/health</c> 此前只报自己的状态——
/// 于是它在**所有后端都挂掉时仍然报健康**，编排系统会继续把流量送进来。
/// 那是本仓最典型的"看起来正常"：进程活着、日志干净、每一个请求都失败。</para>
/// </summary>
/// <param name="probe">可达性探测。</param>
public sealed class ClusterReachabilityHealthCheck(ClusterReachabilityProbe probe) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);

        return result.IsHealthy
            ? HealthCheckResult.Healthy($"全部 {result.ClusterCount} 个 cluster 可达。")
            : HealthCheckResult.Unhealthy("有 cluster 不可达：" + string.Join("；", result.Unreachable));
    }
}
