using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using Microsoft.Extensions.Logging;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// 注册扩展必须产出**可构造的容器**。
///
/// <para><b>为什么需要这组测试。</b>一个注册了、却依赖零实现端口的组件，
/// 在 Production 下**永远不会被发现**——容器只在真正解析它时才报错，而没人解析它。
/// 它只在 Development 下暴露，因为 <c>ValidateOnBuild</c> 那时才打开。</para>
///
/// <para>本仓真的栽过：<c>AddNexusStackInfrastructure</c> 注册了 <c>OutboxPublisher</c>，
/// 而它需要的 <c>IOutboxStore</c> 与 <c>IEventBus</c> **两个都没有实现**。
/// 于是 <c>dotnet run</c>（默认 Development）根本起不来，
/// 而此前所有验证因为**直接跑 DLL（Production）**而全部漏掉了它。</para>
///
/// <para>所以这里用与 <c>dotnet run</c> **相同**的验证选项来构建容器——
/// 一条会失败的检查，而不是"我们跑过没问题"。</para>
/// </summary>
public sealed class CompositionTests
{
    /// <summary>
    /// 与宿主一致的容器验证选项。
    /// <para>Development 下 <c>ValidateOnBuild</c> 与 <c>ValidateScopes</c> 都为真，
    /// 正是它们把上面那个缺陷变成了一次启动失败。</para>
    /// </summary>
    private static readonly ServiceProviderOptions HostLikeValidation = new()
    {
        ValidateOnBuild = true,
        ValidateScopes = true,
    };

    [Fact]
    public void AddNexusStackInfrastructure_ProducesAConstructibleContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 1 });

        using var provider = services.BuildServiceProvider(HostLikeValidation);

        // 容器能建起来就说明每个已注册的服务都能被构造。
        Assert.NotNull(provider.GetRequiredService<IClock>());
    }

    [Fact]
    public void ApplicationAlone_ProducesAConstructibleContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();

        using var provider = services.BuildServiceProvider(HostLikeValidation);

        Assert.NotNull(provider);
    }

    [Fact]
    public void OutboxDelivery_WithoutAnEventBus_FailsLoudly()
    {
        // 投递器需要 IEventBus。没有总线就注册投递器，必须在**构建容器时**就失败，
        // 而不是等到第一条消息要发的时候——那时它只会静静地丢掉消息。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 1 });
        services.AddNexusStackOutboxDelivery();

        var failure = Assert.Throws<AggregateException>(
            () => services.BuildServiceProvider(HostLikeValidation));

        Assert.Contains("IEventBus", failure.InnerException?.InnerException?.Message ?? failure.Message, StringComparison.Ordinal);
    }
}
