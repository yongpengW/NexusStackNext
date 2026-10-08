using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Tests;

public sealed class UserLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Changing_password_revokes_sessions_as_one_aggregate_change()
    {
        var user = User.Register(new UserId(1), UserName.Create("ordinary").Value,
            PasswordHash.Create(new string('a', 64)).Value, Now);
        var replacement = PasswordHash.Create(new string('b', 64)).Value;
        Assert.True(user.ChangePassword(replacement, Now).IsSuccess);
        Assert.Equal(2, user.Version);
        Assert.Equal(1, user.SessionVersion);
        Assert.True(user.ChangePassword(replacement, Now).IsFailure);
        Assert.Equal(2, user.Version);
        Assert.Equal(1, user.SessionVersion);
    }

    [Fact]
    public void Disable_revokes_sessions_once_and_enable_never_restores_them()
    {
        var user = User.Register(new UserId(1), UserName.Create("ordinary").Value,
            PasswordHash.Create(new string('a', 64)).Value, Now);
        Assert.True(user.Disable(Now).IsSuccess);
        Assert.Equal(2, user.Version);
        Assert.Equal(1, user.SessionVersion);
        Assert.True(user.Disable(Now).IsSuccess);
        Assert.Equal(2, user.Version);
        Assert.Equal(1, user.SessionVersion);
        Assert.True(user.Enable(Now).IsSuccess);
        Assert.Equal(3, user.Version);
        Assert.Equal(1, user.SessionVersion);
        Assert.True(user.Enable(Now).IsSuccess);
        Assert.Equal(3, user.Version);
        Assert.Equal(1, user.SessionVersion);
    }
}
