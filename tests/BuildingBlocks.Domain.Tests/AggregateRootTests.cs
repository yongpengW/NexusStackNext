namespace NexusStackNext.BuildingBlocks.Domain.Tests;

/// <summary>
/// 聚合根：领域事件的收集与清空，以及"不变量留在领域内"。
/// </summary>
public sealed class AggregateRootTests
{
    [Fact]
    public void NewAggregate_RaisesCreationEvent()
    {
        var user = User.Register(new UserId(1), UserName.Create("leo").Value, isBuiltIn: true);

        var domainEvent = Assert.Single(user.DomainEvents);
        var registered = Assert.IsType<UserRegistered>(domainEvent);
        Assert.Equal(1, registered.UserId.Value);
        Assert.Equal("leo", registered.UserName);
    }

    [Fact]
    public void EachEvent_GetsItsOwnEventId()
    {
        var user = User.Register(new UserId(1), UserName.Create("leo").Value);

        var first = Assert.Single(user.DomainEvents);
        user.Disable();
        var second = user.DomainEvents.Last();

        Assert.NotEqual(first.EventId, second.EventId);
    }

    [Fact]
    public void ClearDomainEvents_EmptiesTheCollection()
    {
        var user = User.Register(new UserId(1), UserName.Create("leo").Value);
        Assert.NotEmpty(user.DomainEvents);

        user.ClearDomainEvents();

        Assert.Empty(user.DomainEvents);
    }

    [Fact]
    public void DomainEvents_IsReadOnlySnapshot()
    {
        var user = User.Register(new UserId(1), UserName.Create("leo").Value);

        // 外部拿不到可变集合——聚合自己决定宣布什么。
        var events = Assert.IsAssignableFrom<IReadOnlyCollection<IDomainEvent>>(user.DomainEvents);
        Assert.Single(events);
    }

    [Fact]
    public void Disable_BuiltInAccount_ThrowsDomainException()
    {
        // 这条不变量在原项目里住在 RoleService 的一个 if 里，换个调用方就能绕过。
        var builtIn = User.Register(new UserId(1), UserName.Create("admin").Value, isBuiltIn: true);

        var ex = Assert.Throws<DomainException>(builtIn.Disable);

        Assert.Contains("内置账号", ex.Message, StringComparison.Ordinal);
        Assert.True(builtIn.IsEnabled);
    }

    [Fact]
    public void Disable_RegularAccount_FlipsStateAndRaisesEvent()
    {
        var user = User.Register(new UserId(2), UserName.Create("leo").Value);
        user.ClearDomainEvents();

        user.Disable();

        Assert.False(user.IsEnabled);
        Assert.IsType<UserDisabled>(Assert.Single(user.DomainEvents));
    }

    [Fact]
    public void Disable_Twice_RaisesEventOnlyOnce()
    {
        var user = User.Register(new UserId(2), UserName.Create("leo").Value);
        user.Disable();
        user.ClearDomainEvents();

        user.Disable();

        Assert.Empty(user.DomainEvents);
    }

    [Fact]
    public void Aggregate_NeedsNoGlobalState()
    {
        // 这是本票要证明的核心：原项目 new User() 会抛（构造函数调 SnowFlake.Instance，
        // 依赖 App.Init() 之后的静态容器）。这里构造 100 个聚合，不初始化任何东西。
        var users = Enumerable.Range(1, 100)
            .Select(i => User.Register(new UserId(i), UserName.Create($"user{i}").Value))
            .ToList();

        Assert.Equal(100, users.Count);
        Assert.All(users, u => Assert.True(u.IsEnabled));
    }
}
