using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Domain.Events;

/// <summary>用户已注册。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="UserName">用户名。</param>
/// <param name="OccurredAt">发生时刻（UTC），由调用方从时钟取得。</param>
public sealed record UserRegistered(UserId UserId, string UserName, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>用户登录成功。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record UserLoggedIn(UserId UserId, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>登录失败。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="FailedCount">累计失败次数。</param>
/// <param name="LockedUntil">锁定到期时间；未锁定为 <c>null</c>。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record LoginFailed(UserId UserId, int FailedCount, DateTimeOffset? LockedUntil, DateTimeOffset OccurredAt)
    : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>用户被禁用。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record UserDisabled(UserId UserId, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>用户被启用。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record UserEnabled(UserId UserId, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>用户的密码被修改。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record UserPasswordChanged(UserId UserId, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>用户的角色集合发生变化。<b>只在集合真的变了时才发出</b>——空操作不该触发权限缓存失效。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="RoleCount">变更后的角色数量。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record UserRolesChanged(UserId UserId, int RoleCount, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>角色的权限集合发生变化。<b>只在集合真的变了时才发出</b>——空操作不该触发缓存失效。</summary>
/// <param name="RoleId">角色标识。</param>
/// <param name="GrantedMenuCount">变更后的授权菜单数量。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record RolePermissionsChanged(RoleId RoleId, int GrantedMenuCount, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>刷新令牌已签发。</summary>
/// <param name="TokenId">令牌标识。</param>
/// <param name="UserId">用户标识。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record RefreshTokenIssued(RefreshTokenId TokenId, UserId UserId, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>刷新令牌已被撤销。<b>不携带令牌本身</b>，只带标识与原因。</summary>
/// <param name="TokenId">令牌标识。</param>
/// <param name="UserId">用户标识。</param>
/// <param name="Reason">撤销原因。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record RefreshTokenRevoked(RefreshTokenId TokenId, UserId UserId, string Reason, DateTimeOffset OccurredAt)
    : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}
