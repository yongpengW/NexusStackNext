using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Domain.Ids;

/// <summary>用户标识。</summary>
public sealed record UserId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public UserId(long value)
        : base(value)
    {
    }
}

/// <summary>角色标识。</summary>
public sealed record RoleId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public RoleId(long value)
        : base(value)
    {
    }
}

/// <summary>菜单节点标识。</summary>
public sealed record MenuId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public MenuId(long value)
        : base(value)
    {
    }
}

/// <summary>菜单树标识。</summary>
public sealed record MenuTreeId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public MenuTreeId(long value)
        : base(value)
    {
    }
}

/// <summary>API 资源标识。</summary>
public sealed record ApiResourceId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public ApiResourceId(long value)
        : base(value)
    {
    }
}

/// <summary>刷新令牌标识。</summary>
public sealed record RefreshTokenId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public RefreshTokenId(long value)
        : base(value)
    {
    }
}
