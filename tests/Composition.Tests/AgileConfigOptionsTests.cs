using AgileConfig.Client;
using Microsoft.Extensions.Configuration;

namespace NexusStackNext.Composition.Tests;

/// <summary>
/// <c>AgileConfig</c> 配置节 → <see cref="ConfigClientOptions"/> 的映射。
///
/// <para><b>为什么这组测试必须存在。</b>AgileConfig 客户端**不会**自动读取配置节，
/// 映射得自己写。而漏掉字段的后果是**静默的**：应用照常启动、照常拉到配置，
/// 只是 <c>env</c> 为空（多环境配置一条都拉不到）、客户端在后台显示为空白。</para>
///
/// <para>本仓真的踩过：第一版只映射了 <c>AppId</c> / <c>Secret</c> / <c>Nodes</c>，
/// 其余五个字段被丢掉，而**没有任何东西会响**。
/// 所以这里逐个键断言——加字段时忘了写映射，就会有一条红。</para>
/// </summary>
public sealed class AgileConfigOptionsTests
{
    private static ConfigClientOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(static s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var options = new ConfigClientOptions();
        NexusStackHostDefaults.BindOptions(configuration.GetSection(NexusStackHostDefaults.AgileConfigSection), options);
        return options;
    }

    [Fact]
    public void MapsEverySupportedKey()
    {
        var options = Bind(
            ("AgileConfig:AppId", "nexusstack_platform"),
            ("AgileConfig:Secret", "shh"),
            ("AgileConfig:Nodes", "http://config-center:8010"),
            ("AgileConfig:Name", "平台宿主"),
            ("AgileConfig:Tag", "platform"),
            ("AgileConfig:Env", "TEST"),
            ("AgileConfig:Cache:Directory", "agile/config"),
            ("AgileConfig:Cache:Encrypt", "true"),
            ("AgileConfig:ServiceRegister:ServiceId", "nsn-platform"),
            ("AgileConfig:ServiceRegister:ServiceName", "NexusStackNext 平台宿主"));

        Assert.Equal("nexusstack_platform", options.AppId);
        Assert.Equal("shh", options.Secret);
        Assert.Equal("http://config-center:8010", options.Nodes);

        // 运维可见性：后台「客户端」页面靠这两个。
        Assert.Equal("平台宿主", options.Name);
        Assert.Equal("platform", options.Tag);

        // **多环境。** 这一条是这组测试里最重要的：为空时应用照样启动，
        // 但配置中心里按环境分的那一份**一条都拉不到**。
        Assert.Equal("TEST", options.ENV);

        Assert.True(options.CacheEnabled);
        Assert.Equal("agile/config", options.CacheDirectory);
        Assert.True(options.ConfigCacheEncrypt);

        Assert.NotNull(options.RegisterInfo);
        Assert.Equal("nsn-platform", options.RegisterInfo.ServiceId);
        Assert.Equal("NexusStackNext 平台宿主", options.RegisterInfo.ServiceName);
    }

    [Fact]
    public void MissingOptionalKeys_LeaveSensibleDefaults()
    {
        var options = Bind(
            ("AgileConfig:AppId", "app"),
            ("AgileConfig:Secret", "shh"),
            ("AgileConfig:Nodes", "http://node:8010"));

        Assert.Equal("app", options.AppId);
        Assert.Equal(string.Empty, options.Name);
        Assert.Equal(string.Empty, options.Tag);
        Assert.Equal(string.Empty, options.ENV);
        Assert.Null(options.RegisterInfo);
    }

    [Fact]
    public void ServiceRegistration_StaysOff_WhenOnlyOneKeyIsPresent()
    {
        // 只写一半就注册出一个没有名字的服务，比不注册更难查。
        var options = Bind(
            ("AgileConfig:AppId", "app"),
            ("AgileConfig:Nodes", "http://node:8010"),
            ("AgileConfig:ServiceRegister:ServiceId", "nsn-platform"));

        Assert.Null(options.RegisterInfo);
    }

    [Fact]
    public void ServiceRegistration_MapsIpAndPort()
    {
        var options = Bind(
            ("AgileConfig:AppId", "app"),
            ("AgileConfig:Nodes", "http://node:8010"),
            ("AgileConfig:ServiceRegister:ServiceId", "nsn-platform"),
            ("AgileConfig:ServiceRegister:ServiceName", "平台宿主"),
            // 用回环地址：一个**真实的**环境地址（内网或公网）会被
            // scripts/assert-no-credentials.ps1 的"配置里的真实 IP"规则拦下——
            // 那条规则在模板生成物上跑，测试文件也在扫描范围里。
            // 这里改的是测试数据，不是那条规则：规则要挡的正是真实地址。
            ("AgileConfig:ServiceRegister:Ip", "127.0.0.1"),
            ("AgileConfig:ServiceRegister:Port", "5191"));

        Assert.NotNull(options.RegisterInfo);
        Assert.Equal("127.0.0.1", options.RegisterInfo.Ip);
        Assert.Equal(5191, options.RegisterInfo.Port);
    }
}
