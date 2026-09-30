using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;


namespace NexusStackNext.Composition.Tests;

/// <summary>
/// 优先级重排必须在**真实的宿主构建器**上生效。
///
/// <para><b>为什么单独一条。</b>已有的 <c>ConfigurationPriorityTests</c> 用的是普通
/// <see cref="ConfigurationBuilder"/>；而宿主用的是 <c>ConfigurationManager</c>
/// （<c>WebApplicationBuilder.Configuration</c>），它<b>惰性构建提供程序</b>，
/// 对 <c>Sources</c> 的改动是否按新顺序重建，是另一回事。</para>
///
/// <para>本仓踩过：文档里写着"环境变量 &gt; AgileConfig"，测试也过了，
/// 但在真实宿主里环境变量**盖不过**配置中心——因为那条测试测的不是宿主用的那个类型。</para>
/// </summary>
public sealed class ConfigurationOrderingInRealHostTests
{
    [Fact]
    public void InsertedSource_IsOverriddenByEnvironmentVariables_InTheRealHostBuilder()
    {
        // 唯一的键名，避免与并行测试互相干扰。
        var name = "NEXUSSTACK_ORDER_PROBE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "from-env");

        try
        {
            var builder = WebApplication.CreateBuilder();

            // 用内存源代替 AgileConfig（它由 AddNexusStackAgileConfig 追加），
            // 然后追加一个环境变量来源——这正是生产代码用的那个手段。
            builder.Configuration.AddInMemoryCollection([new(name, "from-agile")]);
            builder.Configuration.AddEnvironmentVariables();

            // 环境变量必须赢。
            Assert.Equal("from-env", builder.Configuration[name]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void InsertedSource_StillOverridesAppSettings_InTheRealHostBuilder()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Configuration.AddInMemoryCollection([new("NexusStack:OrderProbe", "from-appsettings")]);

        // 后追加的（模拟 AgileConfig）应当盖过更早的来源。
        builder.Configuration.AddInMemoryCollection([new("NexusStack:OrderProbe", "from-agile")]);

        Assert.Equal("from-agile", builder.Configuration["NexusStack:OrderProbe"]);
    }
}
