using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Application;

/// <summary>用户仓储端口。适配器可以是 EF Core（生产）或内存（开发与测试）。</summary>
public interface IUserRepository
{
    /// <summary>按标识查找。</summary>
    /// <param name="id">用户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回聚合，否则 <c>null</c>。</returns>
    Task<User?> FindAsync(UserId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按用户名查找。
    ///
    /// <para>登录要先按用户名定位用户——这是唯一一条以"人输入的东西"而不是"标识"为起点的读路径。
    /// 它之所以在端口上，是因为登录这条用例必须能问出"这个名字对应谁"，
    /// 而把用户名当成主键去查（<c>FindAsync(UserId)</c>）在语义上是错的。</para>
    /// </summary>
    /// <param name="userName">用户名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户；不存在时为 <c>null</c>。</returns>
    Task<User?> FindByUserNameAsync(UserName userName, CancellationToken cancellationToken = default);

    /// <summary>判断用户名是否已被占用。<b>由仓储回答，而不是"查一遍看看"</b>——那有竞态。</summary>
    /// <param name="userName">用户名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在则 <c>true</c>。</returns>
    Task<bool> UserNameExistsAsync(UserName userName, CancellationToken cancellationToken = default);

    /// <summary>保存新用户。</summary>
    /// <param name="user">用户聚合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task AddAsync(User user, CancellationToken cancellationToken = default);
}

/// <summary>角色仓储端口。</summary>
public interface IRoleRepository
{
    /// <summary>按标识批量查找。找不到的标识被忽略。</summary>
    /// <param name="ids">角色标识集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到的角色。</returns>
    Task<IReadOnlyList<Role>> FindManyAsync(
        IEnumerable<RoleId> ids,
        CancellationToken cancellationToken = default);

    /// <summary>按编码查找。</summary>
    /// <param name="code">角色编码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回聚合，否则 <c>null</c>。</returns>
    Task<Role?> FindByCodeAsync(RoleCode code, CancellationToken cancellationToken = default);

    /// <summary>保存新角色。</summary>
    /// <param name="role">角色聚合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task AddAsync(Role role, CancellationToken cancellationToken = default);
}

/// <summary>API 资源仓储端口。</summary>
public interface IApiResourceRepository
{
    /// <summary>按所属菜单批量查找。</summary>
    /// <param name="menuIds">菜单标识集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>属于这些菜单的端点。</returns>
    Task<IReadOnlyList<ApiResource>> FindByMenuIdsAsync(
        IReadOnlySet<MenuId> menuIds,
        CancellationToken cancellationToken = default);

    /// <summary>保存新资源。</summary>
    /// <param name="resource">API 资源聚合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task AddAsync(ApiResource resource, CancellationToken cancellationToken = default);
}

/// <summary>
/// 菜单树仓储端口。
///
/// <para><b>它此前根本不存在</b>——`MenuTree.AddRoot` 在领域层写好、也有测试，
/// 但**没有任何地方调用它**，因为菜单这一环既没有用例也没有存储。于是权限链断在第一环：
/// 建不出菜单 ⇒ api-resource 挂不上 ⇒ 角色授不到菜单 ⇒ 权限集合永远是空的（票据 67）。</para>
///
/// <para><b>为什么端口上没有标识参数。</b>菜单树是**单例聚合**——整个上下文只有一棵
/// （ADR-0001：整棵树是一个聚合，因为"移动一个节点"必须同时改写它所有后代的物化路径）。
/// 端口上开 `FindAsync(id)` 或者 `Delete`，会暗示"可以有多棵"，而那是另一个设计
/// （多租户？多应用？）——**等真有第二个用例需要它时再加参数**，与不变量 7 同一条道理：
/// 不要为假设的缝留参数。</para>
/// </summary>
public interface IMenuTreeRepository
{
    /// <summary>取菜单树；还没有时返回 <c>null</c>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>菜单树，或 <c>null</c>。</returns>
    Task<MenuTree?> FindAsync(CancellationToken cancellationToken = default);

    /// <summary>保存新树。</summary>
    /// <param name="tree">菜单树聚合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task AddAsync(MenuTree tree, CancellationToken cancellationToken = default);
}
