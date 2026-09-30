namespace NexusStackNext.Identity.Domain;

/// <summary>
/// 用户可登录的平台（可按位组合）。
/// <para>
/// <b>刻意不给 <c>All</c> 赋 0。</b>参照仓库写的是 <c>All = 0</c>（<c>PlatformType.cs:16</c>），
/// 而 <c>0</c> 在按位语义里就是"没有任何平台"。于是同一个值在一处被当作"全部"
/// （<c>PermissionService.cs:48/94</c>）、在另一处被当作"无"（<c>UserRoleService.cs:28</c>）——
/// 这种冲突不会报错，只会让权限判定在某些组合下静默失效。
/// </para>
/// <para>这里把"没有平台"与"全部平台"彻底分开：<see cref="None"/> 是 0，<see cref="All"/> 是全部位的组合。</para>
/// </summary>
[Flags]
public enum Platform
{
    /// <summary>不属于任何平台。**不是**"全部"。</summary>
    None = 0,

    /// <summary>管理后台。</summary>
    Admin = 1 << 0,

    /// <summary>PC 业务系统。</summary>
    Pc = 1 << 1,

    /// <summary>微信小程序。</summary>
    MiniProgram = 1 << 2,

    /// <summary>POS 机 App。</summary>
    Pos = 1 << 3,

    /// <summary>全部平台——全部位的**组合**，不是 0。</summary>
    All = Admin | Pc | MiniProgram | Pos,
}
