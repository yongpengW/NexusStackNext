using System.Collections.Concurrent;
using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application;

/// <summary>
/// 权限的**来源**：把某个用户的有效 API 权限算出来。
///
/// <para>它与缓存分开，是因为两者变化的理由不同：来源变的是**算法**
/// （哪些角色、哪些菜单、哪些资源算数），缓存变的是**存取策略**
/// （单飞、失效、TTL）。把它们揉在一起，任何一边的改动都要重新理解另一边。</para>
/// </summary>
public interface IPermissionSource
{
    /// <summary>算出某个用户当前的有效权限。</summary>
    /// <param name="userId">用户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>权限键集合；用户不存在时是失败。</returns>
    Task<Result<PermissionKeySet>> ReadAsync(UserId userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 权限预计算缓存。
///
/// <para><b>它存在的理由</b>：鉴权在每个请求上跑，而算一次权限要读用户、角色、菜单、API 资源。
/// 把结果收敛成 <c>HashSet&lt;"routetemplate:METHOD"&gt;</c> 之后，鉴权退化成一次集合查找（O(1)）——
/// 这是参照仓库里最值得保留的设计（保留清单 #1）。</para>
///
/// <para><b>它修的三处</b>（见 <see cref="UserPermissionCache"/> 的说明）。</para>
/// </summary>
public interface IPermissionCache
{
    /// <summary>取得某个用户的有效权限（可能来自缓存）。</summary>
    /// <param name="userId">用户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>权限键集合。</returns>
    Task<Result<PermissionKeySet>> GetAsync(UserId userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 让**所有**已缓存的权限失效。
    ///
    /// <para>角色/菜单/API 资源发生变化时调用。它刻意是一个**全局**操作而不是按用户：
    /// 一次菜单授权变更会影响所有拥有该角色的用户，而列举那些用户正是缓存要避免的查询。</para>
    /// </summary>
    void Invalidate();
}

/// <summary>
/// 内存版权限缓存：**单飞 + 版本号失效 + 有限 TTL**。
///
/// <para><b>它修的三处，逐条对应参照仓库的缺陷</b>（review/04）：</para>
/// <list type="number">
/// <item><b>单飞。</b>参照仓库没有——鉴权热路径上缓存击穿时，N 个并发请求会
/// 同时回源，而那正是"最慢的那一刻所有人一起更慢"。这里用
/// <c>Lazy&lt;Task&lt;T&gt;&gt;</c> 的 <c>ExecutionAndPublication</c> 模式：
/// 同一个用户的并发读取共享同一个回源任务。</item>
///
/// <item><b>版本号而不是裸 <c>DEL</c>。</b>裸 <c>DEL</c> 与并发回源之间有竞态：
/// 回源读到旧数据 → 有人 <c>DEL</c> → 回源把**旧值**写回去，而它看起来像一次成功的缓存填充。
/// 改成"失效 = 版本号 +1"之后，旧版本写入的条目**根本不会被读到**——
/// 竞态从"需要小心处理"变成"结构上不可能"。</item>
///
/// <item><b>有限的 TTL。</b>参照仓库的 10 小时 TTL 意味着"禁用立即生效"最长有 10 小时的窗口。
/// 这里降到 <see cref="DefaultTimeToLive"/>（5 分钟）作为**兜底**——
/// 正常路径靠 <see cref="Invalidate"/> 立即生效，TTL 只负责收拾"忘调失效"的漏网之鱼。</item>
/// </list>
///
/// <para><b>它是内存的，这件事必须说清楚。</b>版本号与条目都在进程内，
/// 因此多实例部署时"A 实例失效了、B 实例还拿着旧值"。要解决它需要一个共享的版本存储
/// （Redis 之类）——那是一件**有第二个消费者（第二个实例）时才成立**的改动，
/// 现在做它等于给一个还没出现的需求写适配器。</para>
/// </summary>
public sealed class UserPermissionCache : IPermissionCache
{
    /// <summary>默认有效期。它是**兜底**，正常失效走 <see cref="Invalidate"/>。</summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(5);

    private readonly IPermissionSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeToLive;

    private readonly ConcurrentDictionary<long, Entry> _entries = new();
    private readonly ConcurrentDictionary<long, Lazy<Task<Result<PermissionKeySet>>>> _inFlight = new();

    private long _version;

    /// <summary>
    /// 创建缓存。
    ///
    /// <para><b>来源的生存期由适配器负责。</b>缓存要跨请求存在（Singleton），
    /// 而读取权限要读数据库（EF 的 <c>DbContext</c> 是 Scoped）——把 Scoped 塞进 Singleton
    /// 是**捕获依赖**，会让第一个请求的上下文被后续所有请求共用。
    /// 桥接放在 <c>ScopedPermissionSource</c>（它自己按调用开作用域），
    /// 于是这个类只面对一个普通的端口，单元测试也不必搭容器。</para>
    /// </summary>
    /// <param name="source">权限来源。</param>
    /// <param name="timeProvider">时间源；测试里可替换。</param>
    /// <param name="timeToLive">有效期；默认 <see cref="DefaultTimeToLive"/>。</param>
    public UserPermissionCache(
        IPermissionSource source,
        TimeProvider? timeProvider = null,
        TimeSpan? timeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timeToLive = timeToLive ?? DefaultTimeToLive;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_timeToLive, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public async Task<Result<PermissionKeySet>> GetAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var version = Volatile.Read(ref _version);

        if (TryRead(userId.Value, version, out var cached))
        {
            return cached;
        }

        // **单飞。** 同一个用户的并发读取拿到的是**同一个** Lazy，
        // 因此回源只跑一次；`ExecutionAndPublication` 保证工厂只被执行一次。
        var lazy = _inFlight.GetOrAdd(
            userId.Value,
            _ => new Lazy<Task<Result<PermissionKeySet>>>(
                () => LoadAsync(userId, version, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            // 回源结束后必须移出：否则这个用户**永远**复用第一次的结果，
            // 而失效就成了空操作。这一步是单飞与失效能共存的关键。
            _inFlight.TryRemove(new KeyValuePair<long, Lazy<Task<Result<PermissionKeySet>>>>(userId.Value, lazy));
        }
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        // 只动版本号：**不删条目**。
        // 删条目会与并发回源竞态（回源读到旧值 → 删 → 回源写回旧值）；
        // 而 +1 之后，旧版本写入的条目在下次读取时版本对不上，自然被忽略。
        Interlocked.Increment(ref _version);

        // 顺手清掉已经过期或版本过旧的条目——不是正确性所需，只是别让字典无限长。
        var current = Volatile.Read(ref _version);
        var now = _timeProvider.GetUtcNow();

        foreach (var (key, entry) in _entries)
        {
            if (entry.Version != current || entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private bool TryRead(long userId, long version, out Result<PermissionKeySet> cached)
    {
        cached = default!;

        if (!_entries.TryGetValue(userId, out var entry))
        {
            return false;
        }

        if (entry.Version != version || entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return false;
        }

        cached = Result.Success(entry.Value);
        return true;
    }

    private async Task<Result<PermissionKeySet>> LoadAsync(
        UserId userId,
        long version,
        CancellationToken cancellationToken)
    {
        var result = await _source.ReadAsync(userId, cancellationToken).ConfigureAwait(false);

        // **失败的来源不进缓存。** 把"用户不存在"或一次瞬时故障缓存五分钟，
        // 会让一个本该立刻恢复的问题持续五分钟。
        if (result.IsSuccess)
        {
            _entries[userId.Value] = new Entry(
                version,
                result.Value,
                _timeProvider.GetUtcNow() + _timeToLive);
        }

        return result;
    }

    /// <summary>一条缓存记录。<b>只存成功的结果</b>——失败不进缓存，理由见 <c>LoadAsync</c>。</summary>
    private sealed record Entry(long Version, PermissionKeySet Value, DateTimeOffset ExpiresAt);
}
