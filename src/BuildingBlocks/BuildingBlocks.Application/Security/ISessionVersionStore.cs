using System.Collections.Concurrent;

namespace NexusStackNext.BuildingBlocks.Application.Security;

/// <summary>
/// 每个用户的**会话版本**。
///
/// <para><b>它解决的是无状态令牌唯一解不了的那个问题。</b>访问令牌是自包含的 JWT——
/// 验签方不看数据库就认它。于是"改密码之后旧令牌还能用"、"禁用之后他手上那个还能用到过期"
/// 这类事实，验签本身永远不会发现。</para>
///
/// <para>做法是把版本烤进令牌、每次请求比对当前值：撤销 = 版本 +1，
/// 已发出的令牌立刻对不上号。**不需要维护一张已撤销令牌的名单**——
/// 那会随着撤销次数无界增长，而且每次请求都要查它。</para>
///
/// <para><b>它是内存的，这件事必须说清楚。</b>多实例部署时"A 实例涨了版本、B 实例不知道"，
/// 于是撤销在 B 上不生效。修法是换一个共享实现（Redis 之类）——
/// 而那是一件**有第二个实例时才成立**的改动，现在做它等于给不存在的需求写适配器。</para>
/// </summary>
public interface ISessionVersionStore
{
    /// <summary>读某个用户当前的会话版本；从未涨过时为 <c>0</c>。</summary>
    /// <param name="userId">用户标识。</param>
    /// <returns>版本号。</returns>
    // 不叫 `Get`——CA1716：那个名字与保留关键字冲突，会让别的语言的实现者难受。
    long Read(string userId);

    /// <summary>
    /// **撤销该用户已发出的全部访问令牌。**
    ///
    /// <para>改密码、禁用账号、检测到刷新令牌重放、主动登出时调用。
    /// 它不影响刷新令牌——那个有自己的撤销路径（票据 10），两者互不替代：
    /// 只撤刷新令牌的话，手上的访问令牌还能用到过期；只涨会话版本的话，
    /// 客户端拿刷新令牌又能换一对新的。**要真的赶走一个人，两个都要做。**</para>
    /// </summary>
    /// <param name="userId">用户标识。</param>
    /// <returns>涨过之后的版本号。</returns>
    long Bump(string userId);
}

/// <summary>内存版会话版本。</summary>
public sealed class InMemorySessionVersionStore : ISessionVersionStore
{
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public long Read(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return _versions.TryGetValue(userId, out var version) ? version : 0;
    }

    /// <inheritdoc />
    public long Bump(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return _versions.AddOrUpdate(userId, 1, static (_, current) => current + 1);
    }
}
