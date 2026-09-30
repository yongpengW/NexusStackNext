using NexusStackNext.Platform.Domain.Settings;

namespace NexusStackNext.Platform.Domain.Tests;

/// <summary>Platform 聚合的版本号（ADR-0011）。</summary>
public sealed class AggregateVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameValueAndSameDescription_AreNoOps()
    {
        var setting = GlobalSetting.Create(new SettingId(1), SettingKey.Create("identity.token").Value);
        Assert.Equal(1, setting.Version);

        Assert.True(setting.ChangeValue("30m", Now).IsSuccess);
        Assert.Equal(2, setting.Version);

        // 同值写入是空操作——空操作触发缓存失效会让整个系统无谓抖动，版本同理。
        Assert.True(setting.ChangeValue("30m", Now).IsSuccess);
        Assert.Equal(2, setting.Version);

        setting.Describe("访问令牌有效期");
        Assert.Equal(3, setting.Version);

        setting.Describe("访问令牌有效期");
        Assert.Equal(3, setting.Version);
    }
}
