using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Domain;

/// <summary>
/// Identity 上下文的业务失败。
/// <para>
/// 命名约定：<c>identity.&lt;主体&gt;.&lt;原因&gt;</c>。错误码是**契约**——
/// 客户端与网关据此判断，不要用错误文案做判断依据（文案会变，码不该变）。
/// </para>
/// </summary>
public static class IdentityErrors
{
    /// <summary>用户不存在。</summary>
    /// <returns>错误。</returns>
    public static Error UserNotFound() => new("identity.user.not_found", "用户不存在。");

    /// <summary>账号已被禁用。</summary>
    /// <returns>错误。</returns>
    public static Error UserDisabled() => new("identity.user.disabled", "账号已被禁用。");

    /// <summary>账号被锁定。</summary>
    /// <param name="until">解锁时间。</param>
    /// <returns>错误。</returns>
    public static Error UserLocked(DateTimeOffset until) =>
        new("identity.user.locked", $"账号已被锁定，{until:u} 之后可重试。");

    /// <summary>内置账号受保护。</summary>
    /// <param name="operation">被拒绝的操作。</param>
    /// <returns>错误。</returns>
    public static Error BuiltInAccountProtected(string operation) =>
        new("identity.user.built_in_protected", $"内置账号不允许{operation}。");

    /// <summary>新密码与旧密码相同。</summary>
    /// <returns>错误。</returns>
    public static Error PasswordUnchanged() => new("identity.user.password_unchanged", "新密码不能与当前密码相同。");

    /// <summary>角色不存在。</summary>
    /// <returns>错误。</returns>
    public static Error RoleNotFound() => new("identity.role.not_found", "角色不存在。");

    /// <summary>角色编码重复。</summary>
    /// <param name="code">编码。</param>
    /// <returns>错误。</returns>
    public static Error RoleCodeTaken(string code) => new("identity.role.code_taken", $"角色编码已被占用：{code}。");

    /// <summary>菜单节点不存在。</summary>
    /// <param name="id">节点标识。</param>
    /// <returns>错误。</returns>
    public static Error MenuNotFound(long id) => new("identity.menu.not_found", $"菜单节点不存在：{id}。");

    /// <summary>菜单标识重复。</summary>
    /// <param name="id">节点标识。</param>
    /// <returns>错误。</returns>
    public static Error MenuAlreadyExists(long id) => new("identity.menu.already_exists", $"菜单节点已存在：{id}。");

    /// <summary>不能把节点移动到自己的子树下。</summary>
    /// <returns>错误。</returns>
    public static Error MenuMoveIntoOwnSubtree() =>
        new("identity.menu.move_into_own_subtree", "不能把菜单移动到它自己的子树下——那会产生环。");

    /// <summary>菜单节点还有子节点，不能删除。</summary>
    /// <param name="childCount">子节点数量。</param>
    /// <returns>错误。</returns>
    public static Error MenuHasChildren(int childCount) =>
        new("identity.menu.has_children", $"该菜单下还有 {childCount} 个子节点，请先删除子节点。");

    /// <summary>超出菜单层级上限。</summary>
    /// <param name="maxDepth">上限。</param>
    /// <returns>错误。</returns>
    public static Error MenuDepthExceeded(int maxDepth) =>
        new("identity.menu.depth_exceeded", $"菜单层级不能超过 {maxDepth} 层。");

    /// <summary>权限键重复注册在同一个菜单上。</summary>
    /// <returns>错误。</returns>
    public static Error ApiResourceAlreadyAttached() =>
        new("identity.api_resource.already_attached", "该 API 资源已挂在该菜单上。");

    /// <summary>刷新令牌不可用。</summary>
    /// <param name="reason">原因。</param>
    /// <returns>错误。</returns>
    public static Error RefreshTokenUnusable(string reason) =>
        new("identity.refresh_token.unusable", $"刷新令牌不可用：{reason}。");
}
