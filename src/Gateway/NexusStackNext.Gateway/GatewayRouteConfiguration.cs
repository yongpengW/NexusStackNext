using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Gateway.Routing;
using Yarp.ReverseProxy.Configuration;

namespace NexusStackNext.Gateway;

/// <summary>进程内唯一的路由写入口，串行完成读取、修改、保存和发布。</summary>
/// <param name="initial">启动时已校验的配置。</param>
/// <param name="store">持久化存储。</param>
/// <param name="provider">YARP 配置发布器。</param>
/// <param name="validator">与 YARP 运行时相同的配置校验器。</param>
public sealed class GatewayRouteConfiguration(
    GatewayRouteTable initial,
    IRouteTableStore store,
    InMemoryConfigProvider provider,
    IConfigValidator validator) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GatewayRouteTable _current = initial;

    /// <summary>进程已接受的完整快照；YARP 异步接收变更，正在处理的请求继续使用原快照。</summary>
    public GatewayRouteTable Current => Volatile.Read(ref _current);

    /// <summary>在最新快照上执行一次修改。失败不发布，成功后才释放写锁。</summary>
    /// <param name="change">对最新路由表的修改。</param>
    /// <param name="cancellationToken">保存前可以取消。</param>
    /// <returns>校验或保存的结果。</returns>
    public async Task<Result> UpdateAsync(
        Func<GatewayRouteTable, Result<GatewayRouteTable>> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = change(Current);
            if (candidate.IsFailure)
            {
                return Result.Failure(candidate.Error);
            }

            var (routes, clusters) = YarpConfigMapper.ToYarp(candidate.Value);
            var policies = ValidatePolicies(candidate.Value);
            if (policies.IsFailure)
            {
                return policies;
            }

            var errors = new List<Exception>();
            foreach (var route in routes)
            {
                errors.AddRange(await validator.ValidateRouteAsync(route).ConfigureAwait(false));
            }

            foreach (var cluster in clusters)
            {
                errors.AddRange(await validator.ValidateClusterAsync(cluster).ConfigureAwait(false));
            }

            if (errors.Count > 0)
            {
                return Result.Failure(new Error("gateway.configuration.invalid", string.Join("；", errors.Select(error => error.Message))));
            }

            var saved = await store.SaveAsync(candidate.Value, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                return saved;
            }

            // 落盘成功后不再观察请求取消，必须完成内存发布。
            provider.Update(routes, clusters);
            Volatile.Write(ref _current, candidate.Value);
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>启动与热更新共用的宿主策略白名单。</summary>
    /// <param name="table">候选路由表。</param>
    /// <returns>策略是否受宿主支持。</returns>
    public static Result ValidatePolicies(GatewayRouteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var unknown = table.Routes.Select(route => route.RateLimitPolicy)
            .Where(policy => policy is not null && policy != YarpConfigMapper.DefaultRateLimitPolicy)
            .Distinct(StringComparer.Ordinal).ToArray();
        return unknown.Length == 0 ? Result.Success() : Result.Failure(new Error("gateway.configuration.unknown_policy",
            $"路由表引用了未注册的限流策略：{string.Join("、", unknown)}。当前已注册：{YarpConfigMapper.DefaultRateLimitPolicy}。"));
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
