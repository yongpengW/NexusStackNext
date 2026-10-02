using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Web;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class ClaimsCurrentUserTests
{
    [Fact]
    public void Actor_RequiresAuthentication_AndDoesNotSurviveTheHttpScope()
    {
        var context = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = context };
        var current = new ClaimsCurrentUser(accessor);
        Claim[] claims =
        [
            new("sub", "123456"),
            new(NexusStackClaims.Root, "true"),
            new(NexusStackClaims.Session, "7"),
        ];
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims));
        Assert.Null(current.UserId);
        Assert.False(current.IsRoot);
        Assert.Null(current.SessionVersion);

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        Assert.Equal("123456", current.UserId);
        Assert.True(current.IsRoot);
        Assert.Equal(7, current.SessionVersion);

        accessor.HttpContext = null;
        Assert.Null(current.UserId);
        Assert.False(current.IsRoot);
        Assert.Null(current.SessionVersion);
    }
}
