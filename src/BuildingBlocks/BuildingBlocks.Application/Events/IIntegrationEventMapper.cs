using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>
/// 领域事件 → 集成事件的转换。
///
/// <para><b>为什么这是一道独立的缝，而不是拦截器里的一段映射代码。</b>
/// <see cref="IntegrationEvent"/> 的文档写得很清楚："领域事件（<c>IDomainEvent</c>）与集成事件
/// 不是一回事：前者是进程内的事实，后者是跨上下文、契约化、有版本承诺的消息。
/// <b>二者的转换发生在应用层</b>。"</para>
///
/// <para>把转换放在这里有三个直接后果：</para>
/// <list type="number">
/// <item>基础设施层的拦截器只负责"把转换结果写进 Outbox"，它不认识任何具体事件——
/// 换一个上下文不必改基座。</item>
/// <item>"哪些领域事件对外发布"变成一个**显式的决定**：返回 <c>null</c> 就是不发布。
/// 默认不发布是安全的方向——领域事件往往带着内部细节。</item>
/// <item>这条转换是**纯函数**，因而可以脱离数据库单测。它坐在"事件名与载荷"的契约上，
/// 而那正是 ADR-0007 要求绝对不能靠类型名推导的东西。</item>
/// </list>
/// </summary>
public interface IIntegrationEventMapper
{
    /// <summary>
    /// 把一个领域事件转换成集成事件。
    /// </summary>
    /// <param name="domainEvent">聚合抛出的领域事件。</param>
    /// <returns>要发布的集成事件；<c>null</c> 表示这个领域事件**不对外发布**。</returns>
    IntegrationEvent? Map(IDomainEvent domainEvent);
}
