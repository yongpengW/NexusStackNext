using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application.Tests;

/// <summary>
/// 权限预计算缓存：**单飞、版本失效、有限 TTL**。
///
/// <para>每条各对应参照仓库的一处缺陷（review/04）。</para>
/// </summary>
public sealed class UserPermissionCacheTests
{
    private static readonly UserId User = new(1);

    /// <summary>可计数、可控的权限来源。</summary>
    private sealed class CountingSource : IPermissionSource
    {
        private readonly Lock _gate = new();
        private int _calls;

        /// <summary>回源被调用的次数。</summary>
        public int Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls;
                }
            }
        }

        /// <summary>当前要返回的权限。</summary>
        /// <summary>当前要返回的权限。<b>刻意不叫 `Result`</b>——那会遮蔽 `Result` 类型本身。</summary>
        public PermissionKeySet Granted { get; set; } = PermissionKeySet.FromRaw(["/routetemplate:METHOD"]);

        /// <summary>非空时，回源会等它被释放——用来制造"真正并发"的场景。</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<Result<PermissionKeySet>> ReadAsync(
            UserId userId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _calls++;
            }

            if (Gate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            return Result.Success(Granted);
        }
    }

    /// <summary>**单飞**：N 个并发请求只回源一次。</summary>
    [Fact]
    public async Task ConcurrentReads_GoToTheSourceOnlyOnce()
    {
        var gate = new TaskCompletionSource();
        var source = new CountingSource { Gate = gate };
        var cache = new UserPermissionCache(source);

        // 16 个并发读取——全部落在同一个用户的回源窗口内（回源被 gate 卡住）。
        var readers = Enumerable.Range(0, 16)
            .Select(_ => cache.GetAsync(User))
            .ToArray();

        await Task.Delay(50);

        Assert.Equal(1, source.Calls);

        gate.SetResult();

        var results = await Task.WhenAll(readers);

        Assert.All(results, static result => Assert.True(result.IsSuccess));

        // **关键断言**：16 个并发只回了 1 次源。
        Assert.Equal(1, source.Calls);
    }

    /// <summary>顺序读两次也只回源一次——第二次走缓存。</summary>
    [Fact]
    public async Task SequentialReads_HitTheCache()
    {
        var source = new CountingSource();
        var cache = new UserPermissionCache(source);

        await cache.GetAsync(User);
        await cache.GetAsync(User);

        Assert.Equal(1, source.Calls);
    }

    /// <summary>
    /// **失效立刻生效**：不依赖 TTL 过期。
    ///
    /// <para>参照仓库的失效是裸 <c>DEL</c>，而"禁用立即生效"最长有 10 小时的窗口
    /// （靠 TTL 兜底）。这里失效 = 版本号 +1，下一次读取必然回源。</para>
    /// </summary>
    [Fact]
    public async Task Invalidate_TakesEffectOnTheVeryNextRead()
    {
        var source = new CountingSource();
        var cache = new UserPermissionCache(source);

        var before = await cache.GetAsync(User);

        Assert.Equal(1, source.Calls);
        Assert.Equal("/routetemplate:METHOD", Assert.Single(before.Value.Values));

        // 权限变了，并且调了失效。
        source.Granted = PermissionKeySet.FromRaw(["/api/identity/users:POST"]);
        cache.Invalidate();

        var after = await cache.GetAsync(User);

        Assert.Equal(2, source.Calls);
        Assert.Equal("/api/identity/users:POST", Assert.Single(after.Value.Values));
    }

    /// <summary>
    /// **失效与并发回源之间没有竞态**。
    ///
    /// <para>这正是"版本号而不是裸 <c>DEL</c>"要解决的问题：裸 <c>DEL</c> 下，
    /// 一个已经读到旧数据的回源会把旧值**写回**缓存，而它看起来像一次成功的填充。
    /// 版本号让那次写入直接作废——不需要任何加锁或时序假设。</para>
    /// </summary>
    [Fact]
    public async Task InvalidateDuringAnInFlightLoad_DoesNotResurrectTheOldValue()
    {
        var gate = new TaskCompletionSource();
        var source = new CountingSource { Gate = gate };
        var cache = new UserPermissionCache(source);

        var inFlight = cache.GetAsync(User);

        await Task.Delay(50);
        Assert.Equal(1, source.Calls);

        // 回源还在飞的时候，权限变了并失效。
        source.Granted = PermissionKeySet.FromRaw(["/api/identity/users:POST"]);
        cache.Invalidate();

        // 放行那个**已经在飞**的回源——它带回来的是旧值。
        gate.SetResult();
        await inFlight;

        // 下一次读取必须回源，而不是读到那次旧值。
        source.Gate = null;
        var after = await cache.GetAsync(User);

        Assert.Equal(2, source.Calls);
        Assert.Equal("/api/identity/users:POST", Assert.Single(after.Value.Values));
    }

    /// <summary>
    /// **失败不进缓存**：一次瞬时故障不该被缓存五分钟。
    /// </summary>
    [Fact]
    public async Task Failures_AreNotCached()
    {
        var failing = new FailingSource();
        var cache = new UserPermissionCache(failing);

        Assert.True((await cache.GetAsync(User)).IsFailure);
        Assert.True((await cache.GetAsync(User)).IsFailure);

        // 两次都回源——失败没有被"缓存"成一个五分钟的结论。
        Assert.Equal(2, failing.Calls);
    }

    private sealed class FailingSource : IPermissionSource
    {
        public int Calls { get; private set; }

        public Task<Result<PermissionKeySet>> ReadAsync(
            UserId userId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result.Failure<PermissionKeySet>(
                new Error("identity.user.not_found", "用户不存在。")));
        }
    }
}
