using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>用户可公开的管理与本人资料，不携带凭据或内部安全状态。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="UserName">用户名。</param>
/// <param name="IsEnabled">是否启用。</param>
/// <param name="RoleIds">已分配角色标识，按标识排序。</param>
/// <param name="Version">用户聚合版本。</param>
public sealed record UserView(long UserId, string UserName, bool IsEnabled, IReadOnlyList<long> RoleIds, long Version);

/// <summary>按标识递增的有界用户页；游标只定位下一页，不授予查询权。</summary>
/// <param name="Items">本页用户。</param>
/// <param name="NextAfterUserId">还有下一页时的最后标识，否则为空。</param>
public sealed record UserPage(IReadOnlyList<UserView> Items, long? NextAfterUserId);

/// <summary>所属用户的安全投影读取端口；Memory 与 PostgreSQL 各自限制读取范围。</summary>
public interface IUserDirectory
{
    /// <summary>查找安全投影。</summary>
    /// <param name="userId">用户标识。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>投影，不存在时为空。</returns>
    Task<UserView?> FindAsync(long userId, CancellationToken token);

    /// <summary>读取游标之后最多 limit + 1 条安全投影，额外一条用于判断是否还有下一页。</summary>
    /// <param name="afterUserId">排除的起点标识。</param>
    /// <param name="limit">页大小，1 至 100。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>有序投影。</returns>
    Task<IReadOnlyList<UserView>> ReadAsync(long afterUserId, int limit, CancellationToken token);
}

/// <summary>查询用户目录。默认 50，最大 100；使用稳定标识游标，不提供全表返回。</summary>
/// <param name="AfterUserId">起点标识，默认 0。</param>
/// <param name="Limit">页大小。</param>
public sealed record GetUsersQuery(long AfterUserId = 0, int Limit = 50) : IQuery<UserPage>;

/// <summary>读取管理或本人安全资料。HTTP 决定主体与管理权限。</summary>
/// <param name="UserId">用户标识。</param>
public sealed record GetUserQuery(long UserId) : IQuery<UserView>, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>查询安全用户页。</summary>
/// <param name="directory">所属安全投影。</param>
public sealed class GetUsersHandler(IUserDirectory directory) : IQueryHandler<GetUsersQuery, UserPage>
{
    /// <inheritdoc />
    public async Task<Result<UserPage>> HandleAsync(GetUsersQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.AfterUserId < 0 || query.Limit is < 1 or > 100)
        { return Result.Failure<UserPage>(new("identity.users.page_invalid", "用户页大小必须为 1 至 100，游标不得小于 0。")); }
        var rows = await directory.ReadAsync(query.AfterUserId, query.Limit, cancellationToken).ConfigureAwait(false);
        var items = rows.Take(query.Limit).ToArray();
        return Result.Success(new UserPage(items, rows.Count > query.Limit ? items[^1].UserId : null));
    }
}

/// <summary>读取用户安全投影。</summary>
/// <param name="directory">所属安全投影。</param>
public sealed class GetUserHandler(IUserDirectory directory) : IQueryHandler<GetUserQuery, UserView>
{
    /// <inheritdoc />
    public async Task<Result<UserView>> HandleAsync(GetUserQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var user = await directory.FindAsync(query.UserId, cancellationToken).ConfigureAwait(false);
        return user is null ? Result.Failure<UserView>(IdentityErrors.UserNotFound()) : Result.Success(user);
    }
}
