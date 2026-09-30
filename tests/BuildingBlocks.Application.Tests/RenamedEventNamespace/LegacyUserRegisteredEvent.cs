using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Application.Tests.RenamedEventNamespace;

/// <summary>
/// <b>同一个事件，换了命名空间、换了类型名。</b>
/// 路由键必须完全不变——这是 ADR-0007 要解决的问题（参照仓库拿 CLR 类型全名当路由键，
/// 拆程序集就会静默断链）。类型名与所在的命名空间刻意都与
/// <c>Tests.UserRegisteredEvent</c> 不同。
/// </summary>
internal sealed record LegacyUserRegisteredEvent(Guid UserId) : IntegrationEvent
{
    public override string EventName => "identity.user-registered.v1";
}
