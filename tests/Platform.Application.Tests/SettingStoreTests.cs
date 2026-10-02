using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Platform.Application.Tests;

/// <summary>
/// 配置读写。
/// <para>
/// 重点两条：**写是"不存在就创建"**（调用方不必先问注册过没有，那之间有竞态），
/// 以及同值写入保持已提交版本；领域事实是否对外发布取决于真实消费者与契约映射。
/// </para>
/// </summary>
public sealed class SettingStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static SettingKey Key(string value) => SettingKey.Create(value).Value;

    private static SettingStore NewStore(out InMemorySettingRepository repository)
    {
        repository = new InMemorySettingRepository();
        return new SettingStore(repository, new SequentialIdGenerator(1000), new FixedClock(Now));
    }

    [Fact]
    public async Task PreviouslyReadSetting_RemainsTheObservedVersion_AfterAnotherWrite()
    {
        var store = NewStore(out _);
        var key = Key("mail.sender");
        await store.WriteAsync(key, "original", "original");
        var observed = await store.GetAsync(key);
        Assert.NotNull(observed);
        Assert.True((await store.WriteAsync(key, "changed", "changed", expectedVersion: observed.Version)).IsSuccess);
        Assert.Equal("original", observed.Value);
        Assert.Equal(1, observed.Version);
        var stale = await store.WriteAsync(key, "stale", expectedVersion: observed.Version);
        Assert.Equal(SettingStore.Conflict, stale.Error);
        Assert.Equal("changed", await store.ReadAsync(key));
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var store = NewStore(out _);

        Assert.True((await store.WriteAsync(Key("identity.token.lifetime"), "30m")).IsSuccess);

        Assert.Equal("30m", await store.ReadAsync(Key("identity.token.lifetime")));
    }

    [Fact]
    public async Task FirstWrite_CreatesTheSetting()
    {
        // 调用方**不需要**先问"这个键注册过没有"——那句问话与随后的写之间有竞态。
        var store = NewStore(out var repository);

        await store.WriteAsync(Key("identity.token.lifetime"), "30m");

        Assert.NotNull(await repository.FindAsync(Key("identity.token.lifetime")));
    }

    [Fact]
    public async Task ReadUnknownKey_ReturnsNull_WithoutFailing()
    {
        // "没配过"不是错误。端点把它映射成 200 + value=null；
        // 而"键的格式不对"是错误，映射成 400。
        var store = NewStore(out _);

        Assert.Null(await store.ReadAsync(Key("nothing.here")));
    }

    [Fact]
    public async Task SecondWrite_KeepsTheLatestValue()
    {
        var store = NewStore(out _);
        await store.WriteAsync(Key("identity.token.lifetime"), "30m");

        await store.WriteAsync(Key("identity.token.lifetime"), "45m");

        Assert.Equal("45m", await store.ReadAsync(Key("identity.token.lifetime")));
    }

    [Fact]
    public async Task SameValueWrite_PreservesCommittedVersion()
    {
        var store = NewStore(out _);
        var key = Key("identity.token.lifetime");
        await store.WriteAsync(key, "30m");

        await store.WriteAsync(key, "30m");
        Assert.Equal(1, (await store.GetAsync(key))!.Version);
    }

    [Fact]
    public async Task ChangedValue_CommitsTheNextVersion()
    {
        var store = NewStore(out _);
        var key = Key("identity.token.lifetime");
        await store.WriteAsync(key, "30m");

        await store.WriteAsync(key, "45m");
        var committed = await store.GetAsync(key);
        Assert.NotNull(committed);
        Assert.Equal(2, committed.Version);
        Assert.Equal("45m", committed.Value);
        Assert.Equal("identity.token.lifetime", committed.Key.Value);
    }

    [Fact]
    public async Task ListByScope_DoesNotConfuseSimilarScopes()
    {
        // 这条盯的是前缀匹配那个坑：`identity` 不该命中 `identity-temp`。
        // 参照仓库的 MenuService 用 LIKE '%id%' 犯的是同一类错。
        var store = NewStore(out _);
        await store.WriteAsync(Key("identity.token.lifetime"), "30m");
        await store.WriteAsync(Key("identity-temp.token.lifetime"), "99m");

        var found = await store.ListByScopeAsync("identity");

        Assert.Single(found);
        Assert.Equal("identity.token.lifetime", found[0].Key.Value);
    }

    [Fact]
    public async Task ClearingAValue_KeepsTheSettingRegistered()
    {
        // "没有值"与"没注册过"是不同的状态：前者仍出现在分组列表里。
        var store = NewStore(out _);
        var key = Key("identity.token.lifetime");
        await store.WriteAsync(key, "30m");

        await store.WriteAsync(key, value: null);

        Assert.Null(await store.ReadAsync(key));
        Assert.Single(await store.ListByScopeAsync("identity"));
    }

    [Fact]
    public async Task Write_RejectsNullKey()
    {
        var store = NewStore(out _);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.WriteAsync(null!, "x"));
    }

    [Fact]
    public async Task Read_RejectsNullKey()
    {
        var store = NewStore(out _);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReadAsync(null!));
    }

    [Fact]
    public async Task List_RejectsBlankScope()
    {
        var store = NewStore(out _);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListByScopeAsync("  "));
    }

}
