using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;

using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure;

/// <summary>
/// 基础设施层注册。
/// <para>
/// <b>WorkerId 与投递策略都由调用方显式传入，不从环境里偷偷读。</b>
/// 参照仓库各服务从本地 appsettings 各拿一个 WorkerId，重复了也不会有任何提示，
/// 要等到写库主键冲突才暴露。让宿主把配置读出来再传进来（<c>AGENTS.md</c> 不变量 8），
/// 这个决定就看得见、可审阅。
/// </para>
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册基础设施层。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="idGeneratorOptions">ID 生成配置，<b>WorkerId 必须由部署层统一分配</b>。</param>
    /// <param name="outboxOptions">Outbox 投递策略；不传则用默认值。</param>
    /// <returns>同一个服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    public static IServiceCollection AddNexusStackInfrastructure(
        this IServiceCollection services,
        IdGeneratorOptions idGeneratorOptions,
        OutboxDeliveryOptions? outboxOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(idGeneratorOptions);

        var delivery = outboxOptions ?? OutboxDeliveryOptions.Default;
        delivery.Validate();

        // 依赖 AddNexusStackApplication 注册的 IClock；顺序反了会在这里立刻失败，而不是在运行时。
        services.TryAddSingleton<IIdGenerator>(provider => new SnowflakeIdGenerator(
            idGeneratorOptions,
            provider.GetRequiredService<IClock>()));

        services.TryAddSingleton(delivery);
        services.TryAddSingleton<IIntegrationEventSerializer, SystemTextJsonIntegrationEventSerializer>();

        // 发件箱的内存适配器必须一起注册：收件箱一直有内存适配器，发件箱此前没有。
        // 那处不对称让 OutboxPublisher 依赖一个零实现的端口——Production 下没人验证得出来，
        // 而 Development（dotnet run 的默认环境）下 ValidateOnBuild 会当场抛异常。
        services.TryAddSingleton<IOutboxStore, InMemoryOutboxStore>();

        // **刻意不在这里注册 OutboxPublisher。**
        //
        // 它还需要一个 IEventBus。**在此之前那个端口没有实现**（票据 21 要接 RabbitMQ），
        // 而现在有了：RabbitMqEventBus。宿主在配了 RabbitMQ 时调 AddNexusStackRabbitMqEventBus()。
        // 三条路都试过了，只有一条是对的：
        //   · 在这里注册它 → 依赖缺失，Development 下容器验证直接抛异常，宿主起不来。
        //   · 塞一个空实现的事件总线 → **比不注册更糟**：它会把每一条消息静静丢掉，
        //     而"发了但没人收到"正是这个项目反复批判的失败模式（评审 04 F2）。
        //   · 把投递器做成显式开关（本方案）→ 没有总线就没有投递器，事实清楚。
        //
        // 总线接上之后，宿主再调用 AddNexusStackOutboxDelivery()。

        return services;
    }

    /// <summary>
    /// 注册 Outbox **投递循环**。
    ///
    /// <para><b>必须在一个 <c>IEventBus</c> 实现已经注册之后才调用。</b>
    /// 它单独成一个方法，而不是并进 <see cref="AddNexusStackInfrastructure"/>，
    /// 是因为"有没有投递循环"取决于"有没有能投递的总线"——
    /// 而那是一个宿主级的、显式的决定，不该被藏在基础设施注册里。</para>
    ///
    /// <para>忘了注册总线就调用它会**立刻失败**（Development 下容器验证会抛），
    /// 而不是等到第一条消息要发的时候才发现。</para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合，便于链式调用。</returns>
    public static IServiceCollection AddNexusStackOutboxDelivery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<OutboxPublisher>();

        // **循环也在这里注册。** 第 16 轮 review 发现：投递器、策略、八档退避都写完了，
        // 但**没有任何东西在调用它**——outbox 里的记录会永远躺着，
        // 而"没发出去"与"没有要发的"长得一模一样。
        services.AddHostedService<OutboxDeliveryWorker>();

        return services;
    }
    /// <summary>
    /// 接上 RabbitMQ 事件总线，并**随之启用发件箱投递**。
    ///
    /// <para><b>两件事必须一起做，所以它们在一个方法里。</b>接总线而不排空发件箱，
    /// 消息会躺在表里永远不动；排空发件箱而不接总线，投递器依赖一个缺失的端口，
    /// Development 下容器验证当场抛。它们是一件事的两半。</para>
    ///
    /// <para><b>只在配了 RabbitMQ 时调用。</b>没配却接上一个总线，
    /// 结果是一个"每次发布都失败"的总线——那比不接更糟：投递器会把每条消息
    /// 记成一次失败尝试，八轮之后全部进死信。**没有总线的正确表现是没有投递器**，
    /// 而不是一个永远失败的总线。</para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="options">RabbitMQ 连接配置。</param>
    /// <returns>同一个服务集合，便于链式调用。</returns>
    public static IServiceCollection AddNexusStackRabbitMqEventBus(
        this IServiceCollection services,
        RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<IEventBus>(new RabbitMqEventBus(options));

        return services.AddNexusStackOutboxDelivery();
    }
}
