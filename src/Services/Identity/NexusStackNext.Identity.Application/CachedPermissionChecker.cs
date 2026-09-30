using System.Globalization;
using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application;

/// <summary>
/// 用票据 08 的预计算缓存回答"这个人有哪些权限"。
///
/// <para><b>它存在的意义是把"谁回答权限"这件事挡在 Identity 里面。</b>
/// 授权过滤器要被五个上下文共用，而权限缓存、领域类型、用户表都属于 Identity——
/// 端口让过滤器只认识一个字符串标识与一个键集合。</para>
///
/// <para><b>它顺带解决了撤销</b>：过滤器**每个请求都问这里**，而这里走缓存、
/// 缓存有版本号失效（票据 08）。所以"授权变更立即生效"在过滤器上是自动成立的，
/// 不需要过滤器自己知道任何失效逻辑。</para>
/// </summary>
/// <param name="cache">权限预计算缓存。</param>
public sealed class CachedPermissionChecker(IPermissionCache cache) : IPermissionChecker
{
    /// <inheritdoc />
    public Task<Result<PermissionKeySet>> ReadAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult(Result.Failure<PermissionKeySet>(
                new Error("identity.user.missing_id", "令牌里没有用户标识。")));
        }

        // 声明里的是字符串——它来自令牌，而令牌是外部输入。
        // 解析失败要当成"问不出来"，不是"没有权限"：两者的诊断方向不同。
        if (!long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return Task.FromResult(Result.Failure<PermissionKeySet>(
                new Error("identity.user.malformed_id", $"令牌里的用户标识不是合法的数字：{userId}。")));
        }

        return cache.GetAsync(new UserId(value), cancellationToken);
    }
}
