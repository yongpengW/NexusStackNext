using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain.Authorization;

namespace NexusStackNext.BuildingBlocks.Application.Tests;

/// <summary>
/// 授权判定。<b>重点是"默认拒绝"能被穷举验证</b>，而不是靠读配置代码来相信它。
/// </summary>
public sealed class AccessPolicyTests
{
    private static readonly PermissionKey Granted = PermissionKey.From("/api/users", "GET");
    private static readonly PermissionKey Other = PermissionKey.From("/api/roles", "GET");

    private static PermissionKeySet SetWith(params PermissionKey[] keys) => PermissionKeySet.From(keys);

    [Fact]
    public void DenyAll_IsTheDefaultEnumValue()
    {
        // 配置缺失、枚举未赋值、反序列化失败——任何一条路径都落到"拒绝"。
        Assert.Equal(0, (int)AuthorizationMode.DenyAll);
        Assert.Equal(AuthorizationMode.DenyAll, default);
    }

    [Theory]
    [InlineData(AuthorizationMode.DenyAll)]
    [InlineData(AuthorizationMode.RootOnly)]
    [InlineData(AuthorizationMode.Authenticated)]
    [InlineData(AuthorizationMode.PermissionKey)]
    public void Unauthenticated_AlwaysAnswers401(AuthorizationMode mode)
    {
        // 不要用 403 掩盖"你还没登录"。
        var decision = AccessPolicy.Decide(
            isAuthenticated: false,
            isRoot: false,
            granted: null,
            required: Granted,
            mode);

        Assert.Equal(AccessDecision.Unauthenticated, decision);
    }

    [Fact]
    public void DenyAll_DeniesEvenRoot()
    {
        var decision = AccessPolicy.Decide(true, isRoot: true, SetWith(Granted), Granted, AuthorizationMode.DenyAll);

        Assert.Equal(AccessDecision.Forbidden, decision);
    }

    [Fact]
    public void RootOnly_AllowsOnlyRoot()
    {
        Assert.Equal(
            AccessDecision.Allowed,
            AccessPolicy.Decide(true, isRoot: true, null, null, AuthorizationMode.RootOnly));

        Assert.Equal(
            AccessDecision.Forbidden,
            AccessPolicy.Decide(true, isRoot: false, SetWith(Granted), null, AuthorizationMode.RootOnly));
    }

    [Fact]
    public void Authenticated_AllowsAnyAuthenticatedUser()
    {
        var decision = AccessPolicy.Decide(true, isRoot: false, null, null, AuthorizationMode.Authenticated);

        Assert.Equal(AccessDecision.Allowed, decision);
    }

    [Fact]
    public void PermissionKey_AllowsWhenKeyIsGranted()
    {
        var decision = AccessPolicy.Decide(true, false, SetWith(Granted), Granted, AuthorizationMode.PermissionKey);

        Assert.Equal(AccessDecision.Allowed, decision);
    }

    [Fact]
    public void PermissionKey_DeniesWhenKeyIsMissing()
    {
        var decision = AccessPolicy.Decide(true, false, SetWith(Other), Granted, AuthorizationMode.PermissionKey);

        Assert.Equal(AccessDecision.Forbidden, decision);
    }

    [Fact]
    public void PermissionKey_WithNoDeclaredRequirement_Denies()
    {
        // 端点没声明要求就是拒绝：公开端点应当走 Authenticated 模式**显式**表达，
        // 而不是靠"忘了标注"这种默认放行。
        var decision = AccessPolicy.Decide(true, false, SetWith(Granted), required: null, AuthorizationMode.PermissionKey);

        Assert.Equal(AccessDecision.Forbidden, decision);
    }

    [Fact]
    public void PermissionKey_WithNoGrantedSet_Denies()
    {
        // 缓存未命中、用户无任何角色——未知的权限集不能当成"什么都能做"。
        var decision = AccessPolicy.Decide(true, false, granted: null, Granted, AuthorizationMode.PermissionKey);

        Assert.Equal(AccessDecision.Forbidden, decision);
    }

    [Fact]
    public void PermissionKey_RootBypassesTheKeyLookup()
    {
        var decision = AccessPolicy.Decide(true, isRoot: true, PermissionKeySet.Empty, Granted, AuthorizationMode.PermissionKey);

        Assert.Equal(AccessDecision.Allowed, decision);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public void UnknownMode_FailsClosed(int rawMode)
    {
        // fail-closed 的最后一道：将来加了新模式却忘了在这里处理，默认也是拒绝。
        var decision = AccessPolicy.Decide(true, true, SetWith(Granted), Granted, (AuthorizationMode)rawMode);

        Assert.Equal(AccessDecision.Forbidden, decision);
    }

    [Fact]
    public void EveryModeIsHandled_NoSilentFallthrough()
    {
        // 枚举里每一个已定义的值都必须被显式处理过——加新模式时这条会提醒你回到 Decide。
        foreach (var mode in Enum.GetValues<AuthorizationMode>())
        {
            var decision = AccessPolicy.Decide(true, isRoot: true, SetWith(Granted), Granted, mode);

            Assert.True(
                decision is AccessDecision.Allowed or AccessDecision.Forbidden or AccessDecision.Unauthenticated,
                $"模式 {mode} 返回了意料之外的判定。");
        }
    }
}
