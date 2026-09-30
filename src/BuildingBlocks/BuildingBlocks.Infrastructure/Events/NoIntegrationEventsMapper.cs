using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// 当前事实的映射器：<b>把每一条领域事件都映射成"不发布"</b>。
///
/// <para><b>它不是占位符，也不是 TODO。</b>它记录的是本仓此刻的形态：
/// 五个平台能力在同一个进程里被一个宿主组装，彼此通过端口与 <c>ICurrentUser</c> 协作，
/// <b>还没有任何上下文发布过集成事件</b>（见 <c>CONTEXT-MAP.md</c> 的"还没建的"一节）。
/// 事件总线与发件箱已经建好并验过（6 条真 broker 验收），但**没有生产者**。</para>
///
/// <para><b>为什么仍然要注册一个。</b>因为"没有映射器"与"映射器把一切都映射成不发布"
/// 是两种不同的状态，而它们此前长得一样：<c>DomainEventOutboxInterceptor</c> 需要一个
/// <see cref="IIntegrationEventMapper"/> 才能被构造，没有实现时它**根本挂不上去**——
/// 于是审计字段也一起不写了（两个拦截器接在同一处装配上）。
/// 注册这个映射器之后，"不发布"变成一条**写下来的决定**，而不是一次缺席；
/// 同时发件箱那条链路（收集 → 映射 → 入队 → 投递循环）在任何上下文发出第一个事件时就已经通了。</para>
///
/// <para><b>第一个真正的事件出现时怎么办。</b>那个上下文注册自己的映射器（覆盖这一条），
/// 并在 <c>CONTEXT-MAP.md</c> 里把对应的条目从"还没建的"移到"已经成立的"。</para>
/// </summary>
public sealed class NoIntegrationEventsMapper : IIntegrationEventMapper
{
    /// <inheritdoc />
    public IntegrationEvent? Map(IDomainEvent domainEvent) => null;
}
