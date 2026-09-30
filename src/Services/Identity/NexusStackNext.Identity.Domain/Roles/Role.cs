using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Events;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Domain.Roles;

/// <summary>角色编码：稳定、机器可读、不随展示名变化。</summary>
public sealed class RoleCode : ValueObject
{
    /// <summary>最大长度。</summary>
    public const int MaxLength = 64;

    private RoleCode(string value) => Value = value;

    /// <summary>规范化后的编码（小写）。</summary>
    public string Value { get; }

    /// <summary>构造角色编码。只允许小写字母、数字、点、短横线与下划线。</summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<RoleCode> Create(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<RoleCode>(new Error("identity.role_code.empty", "角色编码不能为空。"));
        }

        if (trimmed.Length > MaxLength)
        {
            return Result.Failure<RoleCode>(new Error("identity.role_code.too_long", $"角色编码不能超过 {MaxLength} 个字符。"));
        }

        return trimmed.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? Result.Success(new RoleCode(trimmed))
            : Result.Failure<RoleCode>(new Error(
                "identity.role_code.format",
                "角色编码只允许小写字母、数字、点、短横线与下划线。"));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>角色展示名。</summary>
public sealed class RoleName : ValueObject
{
    /// <summary>最大长度。</summary>
    public const int MaxLength = 64;

    private RoleName(string value) => Value = value;

    /// <summary>规范化后的名称。</summary>
    public string Value { get; }

    /// <summary>构造角色名。</summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<RoleName> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<RoleName>(new Error("identity.role_name.empty", "角色名称不能为空。"));
        }

        return trimmed.Length > MaxLength
            ? Result.Failure<RoleName>(new Error("identity.role_name.too_long", $"角色名称不能超过 {MaxLength} 个字符。"))
            : Result.Success(new RoleName(trimmed));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// 角色聚合根，同时是"授权"的一致性边界：它对哪些菜单有权限。
/// <para>
/// <b>系统内置角色不允许删除</b>。参照仓库把这条写在服务里（<c>RoleService.cs:32-35</c> 的
/// <c>if (entity.IsSystem) throw</c>），换个调用方就能绕过去。这里它是聚合自己的方法：
/// 任何删除路径都必须先问过它。
/// </para>
/// <para>
/// 权限变更<b>只在集合真的变了时</b>才发 <see cref="RolePermissionsChanged"/>——
/// 空操作触发事件会导致缓存无谓失效，而参照仓库的失效路径本就最长有 10 小时窗口（review/04）。
/// </para>
/// </summary>
public sealed class Role : AggregateRoot<RoleId>
{
    private readonly HashSet<MenuId> _grantedMenuIds = [];

    private Role(RoleId id, RoleCode code, RoleName name, Platform platforms, bool isSystem)
        : base(id)
    {
        Code = code;
        Name = name;
        Platforms = platforms;
        IsSystem = isSystem;
    }

    /// <summary>角色编码，创建后不可变。</summary>
    public RoleCode Code { get; }

    /// <summary>角色展示名。</summary>
    public RoleName Name { get; private set; }

    /// <summary>该角色适用的平台。</summary>
    public Platform Platforms { get; private set; }

    /// <summary>是否系统内置角色。</summary>
    public bool IsSystem { get; }

    /// <summary>已授权的菜单标识（有序，便于比较与断言）。</summary>
    public IReadOnlyList<MenuId> GrantedMenuIds => [.. _grantedMenuIds.OrderBy(static id => id.Value)];

    /// <summary>创建一个普通角色。</summary>
    /// <param name="id">标识。</param>
    /// <param name="code">编码。</param>
    /// <param name="name">名称。</param>
    /// <param name="platforms">适用平台。</param>
    /// <returns>角色聚合。</returns>
    public static Role Create(RoleId id, RoleCode code, RoleName name, Platform platforms = Platform.None) =>
        new(id, code, name, platforms, isSystem: false);

    /// <summary>创建一个系统内置角色。</summary>
    /// <param name="id">标识。</param>
    /// <param name="code">编码。</param>
    /// <param name="name">名称。</param>
    /// <param name="platforms">适用平台。</param>
    /// <returns>角色聚合。</returns>
    public static Role CreateSystem(RoleId id, RoleCode code, RoleName name, Platform platforms = Platform.None) =>
        new(id, code, name, platforms, isSystem: true);

    /// <summary>
    /// 断言本角色可以被删除。
    /// <para>删除动作本身由应用层执行（仓储），但<b>必须</b>先调用这里——
    /// 不变量属于聚合，不属于调用方。</para>
    /// </summary>
    /// <exception cref="DomainException">系统内置角色。</exception>
    public void EnsureDeletable()
    {
        if (IsSystem)
        {
            throw new DomainException($"系统内置角色不允许删除：{Code.Value}。");
        }
    }

    /// <summary>改名。传入相同的名字不是改变。</summary>
    /// <param name="name">新名称。</param>
    public void Rename(RoleName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Name.Equals(name))
        {
            return;
        }

        Name = name;
        BumpVersion();
    }

    /// <summary>改变适用平台。传入相同的集合不是改变。</summary>
    /// <param name="platforms">新平台集合。</param>
    public void ChangePlatforms(Platform platforms)
    {
        if (Equals(Platforms, platforms))
        {
            return;
        }

        Platforms = platforms;
        BumpVersion();
    }

    /// <summary>授予一个菜单权限。已授权时不重复发事件。</summary>
    /// <param name="menuId">菜单标识。</param>
    /// <param name="at">操作时刻。</param>
    /// <returns>成功。</returns>
    public Result Grant(MenuId menuId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(menuId);

        return _grantedMenuIds.Add(menuId) ? RaisePermissionsChanged(at) : Result.Success();
    }

    /// <summary>撤销一个菜单权限。未授权时不重复发事件。</summary>
    /// <param name="menuId">菜单标识。</param>
    /// <param name="at">操作时刻。</param>
    /// <returns>成功。</returns>
    public Result Revoke(MenuId menuId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(menuId);

        return _grantedMenuIds.Remove(menuId) ? RaisePermissionsChanged(at) : Result.Success();
    }

    /// <summary>整体替换授权集合。</summary>
    /// <param name="menuIds">新的授权集合。</param>
    /// <param name="at">操作时刻。</param>
    /// <returns>成功。</returns>
    /// <exception cref="ArgumentException">集合中包含空标识（那是调用方的编程错误，不是业务失败）。</exception>
    public Result ReplaceGrants(IEnumerable<MenuId> menuIds, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(menuIds);

        // 先查空标识再建集合：一是省掉一次无谓的分配，二是**判据与文档一致**——
        // 这个方法抛 `ArgumentException`（编程错误），而不是返回 `Result.Failure`（业务失败），
        // 原来的 XML 文档却写着"或集合中存在空标识"作为返回值，读起来像业务失败。
        foreach (var menuId in menuIds)
        {
            if (menuId is null)
            {
                throw new ArgumentException("授权集合中不能包含空标识。", nameof(menuIds));
            }
        }

        var target = menuIds.ToHashSet();

        if (target.SetEquals(_grantedMenuIds))
        {
            return Result.Success();
        }

        _grantedMenuIds.Clear();
        foreach (var menuId in target)
        {
            _grantedMenuIds.Add(menuId);
        }

        return RaisePermissionsChanged(at);
    }

    private Result RaisePermissionsChanged(DateTimeOffset at)
    {
        Raise(new RolePermissionsChanged(Id, _grantedMenuIds.Count, at));
        return Changed();
    }
}
