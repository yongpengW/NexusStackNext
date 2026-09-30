using NexusStackNext.Platform.Domain.Settings;

namespace NexusStackNext.Platform.Domain.Tests;

/// <summary>Platform：配置键的结构，以及"空操作不发事件"。</summary>
public sealed class PlatformTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SettingKey_SplitsScopeAndName()
    {
        var key = SettingKey.Create("identity.token.lifetime").Value;

        Assert.Equal("identity", key.Scope);
        Assert.Equal("token.lifetime", key.Name);
        Assert.Equal("identity.token.lifetime", key.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("has space.key")]
    public void SettingKey_RejectsMalformed(string? input)
    {
        Assert.True(SettingKey.Create(input).IsFailure);
    }

    [Fact]
    public void SettingKey_ScopeIsStructural_NotAStringPrefix()
    {
        // 按前缀匹配时 identity 会意外命中 identity-temp；按段比较不会。
        var identity = SettingKey.Create("identity.token").Value;
        var identityTemp = SettingKey.Create("identity-temp.token").Value;

        Assert.NotEqual(identity.Scope, identityTemp.Scope);
        Assert.NotEqual(identity, identityTemp);
    }

    [Fact]
    public void ChangeValue_RaisesEventOnce_AndIsNoOpForSameValue()
    {
        var setting = GlobalSetting.Create(new SettingId(1), SettingKey.Create("identity.token.lifetime").Value, "30m");

        Assert.True(setting.ChangeValue("60m", Now).IsSuccess);
        Assert.IsType<GlobalSettingChanged>(Assert.Single(setting.DomainEvents));

        // 空操作触发缓存失效会让整个系统无谓抖动。
        setting.ClearDomainEvents();
        Assert.True(setting.ChangeValue("60m", Now).IsSuccess);
        Assert.Empty(setting.DomainEvents);
    }
}
