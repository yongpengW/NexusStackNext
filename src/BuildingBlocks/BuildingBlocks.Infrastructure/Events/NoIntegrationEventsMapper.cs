using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// 默认不把领域事件直接公开为集成事件。宿主可显式注册契约映射；
/// 当前 Costing 与 Platform 在自己的提交路径登记经过裁剪的 Outbox 契约，
/// 不将内部领域事件或业务字段值自动序列化到总线。
/// </summary>
public sealed class NoIntegrationEventsMapper : IIntegrationEventMapper
{
    /// <inheritdoc />
    public IntegrationEvent? Map(IDomainEvent domainEvent) => null;
}
