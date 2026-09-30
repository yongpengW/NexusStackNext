using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Events;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Tests;

/// <summary>
/// 用户聚合。重点：内置账号受保护、锁定与解锁的边界、以及"不依赖任何全局状态就能构造"。
/// </summary>
public sealed class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static PasswordHash Hash(string seed = "a") =>
        PasswordHash.Create(new string(seed[0], 64)).Value;

    private static User NewUser(bool builtIn = false, string name = "leo") =>
        User.Register(new UserId(1), UserName.Create(name).Value, Hash(), Now, builtIn);

    [Fact]
    public void Register_RaisesUserRegistered_WithName()
    {
        var user = NewUser();

        var registered = Assert.IsType<UserRegistered>(Assert.Single(user.DomainEvents));
        Assert.Equal(1, registered.UserId.Value);
        Assert.Equal("leo", registered.UserName);
        Assert.Equal(Now, registered.OccurredAt);
    }

    [Fact]
    public void Register_NeedsNoGlobalState()
    {
        // 参照仓库的 new User() 会抛：构造函数里调 SnowFlake.Instance，依赖 App.Init() 之后的静态容器。
        var users = Enumerable.Range(1, 100)
            .Select(i => User.Register(
                new UserId(i),
                UserName.Create($"user{i}").Value,
                Hash(),
                Now))
            .ToList();

        Assert.Equal(100, users.Count);
        Assert.All(users, u => Assert.True(u.IsEnabled));
    }

    [Fact]
    public void Register_WithNullId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            User.Register(null!, UserName.Create("leo").Value, Hash(), Now));
    }

    [Fact]
    public void Disable_BuiltInAccount_IsRejected_AndAccountStaysEnabled()
    {
        // 内置账号是平台的根管理员，禁用它会把所有人锁在门外。
        var user = NewUser(builtIn: true);

        var result = user.Disable(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user.built_in_protected", result.Error.Code);
        Assert.True(user.IsEnabled);
    }

    [Fact]
    public void Disable_RegularAccount_RaisesEvent_AndIsIdempotent()
    {
        var user = NewUser();
        user.ClearDomainEvents();

        Assert.True(user.Disable(Now).IsSuccess);
        Assert.False(user.IsEnabled);
        Assert.IsType<UserDisabled>(Assert.Single(user.DomainEvents));

        user.ClearDomainEvents();
        Assert.True(user.Disable(Now).IsSuccess);
        Assert.Empty(user.DomainEvents);
    }

    [Fact]
    public void Enable_AfterDisable_RaisesEvent_AndClearsLock()
    {
        var user = NewUser();
        user.RecordFailedLogin(Now, LockoutPolicy.Default);
        user.Disable(Now);
        user.ClearDomainEvents();

        Assert.True(user.Enable(Now).IsSuccess);

        Assert.True(user.IsEnabled);
        Assert.Null(user.LockedUntil);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.IsType<UserEnabled>(Assert.Single(user.DomainEvents));
    }

    [Fact]
    public void EnsureCanAuthenticate_WhenDisabled_Fails()
    {
        var user = NewUser();
        user.Disable(Now);

        var result = user.EnsureCanAuthenticate(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user.disabled", result.Error.Code);
    }

    [Fact]
    public void RecordFailedLogin_LocksExactlyAtThreshold()
    {
        // 边界：阈值前一次不锁，达到阈值才锁。
        var policy = new LockoutPolicy(3, TimeSpan.FromMinutes(10));
        var user = NewUser();

        user.RecordFailedLogin(Now, policy);
        user.RecordFailedLogin(Now, policy);
        Assert.Null(user.LockedUntil);
        Assert.True(user.EnsureCanAuthenticate(Now).IsSuccess);

        user.RecordFailedLogin(Now, policy);
        Assert.Equal(Now + TimeSpan.FromMinutes(10), user.LockedUntil);
        Assert.True(user.EnsureCanAuthenticate(Now).IsFailure);
    }

    [Fact]
    public void EnsureCanAuthenticate_UnlocksAtExactExpiry()
    {
        // 边界：锁定到期的那一刻就应当可以登录（用 >= 而不是 >）。
        var policy = new LockoutPolicy(1, TimeSpan.FromMinutes(10));
        var user = NewUser();
        user.RecordFailedLogin(Now, policy);

        Assert.True(user.EnsureCanAuthenticate(Now + TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1)).IsFailure);
        Assert.True(user.EnsureCanAuthenticate(Now + TimeSpan.FromMinutes(10)).IsSuccess);
    }

    [Fact]
    public void RecordSuccessfulLogin_ResetsFailuresAndLock_AndRaisesEvent()
    {
        var user = NewUser();
        user.RecordFailedLogin(Now, new LockoutPolicy(5, TimeSpan.FromMinutes(10)));
        user.ClearDomainEvents();

        user.RecordSuccessfulLogin(Now);

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockedUntil);
        Assert.Equal(Now, user.LastLoginAt);
        Assert.IsType<UserLoggedIn>(Assert.Single(user.DomainEvents));
    }

    [Fact]
    public void ChangePassword_SameHash_IsRejected()
    {
        var user = NewUser();
        var same = user.PasswordHash;

        var result = user.ChangePassword(same, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user.password_unchanged", result.Error.Code);
    }

    [Fact]
    public void ChangePassword_RaisesEvent_AndUpdatesHash()
    {
        var user = NewUser();
        user.ClearDomainEvents();
        var newHash = Hash("b");

        Assert.True(user.ChangePassword(newHash, Now).IsSuccess);

        Assert.Equal(newHash, user.PasswordHash);
        Assert.IsType<UserPasswordChanged>(Assert.Single(user.DomainEvents));
    }

    [Fact]
    public void LockoutPolicy_RejectsNonPositiveValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LockoutPolicy.Validate(new LockoutPolicy(0, TimeSpan.FromMinutes(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => LockoutPolicy.Validate(new LockoutPolicy(1, TimeSpan.Zero)));
    }
}
