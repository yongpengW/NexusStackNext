using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
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
