using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Roles;

namespace NexusStackNext.Identity.Application;

/// <summary>角色选择与管理所需的目录项。</summary>
/// <param name="RoleId">角色标识。</param>
/// <param name="Code">稳定编码。</param>
/// <param name="Name">展示名。</param>
/// <param name="Version">条件写入版本。</param>
/// <param name="IsEnabled">是否参与当前授权。</param>
public sealed record RoleDirectoryItem(long RoleId, string Code, string Name, long Version, bool IsEnabled);

/// <summary>按稳定标识递增的角色页。</summary>
/// <param name="Items">目录项。</param>
/// <param name="NextAfterRoleId">下一页游标；没有更多项时为空。</param>
public sealed record RolePage(IReadOnlyList<RoleDirectoryItem> Items, long? NextAfterRoleId);

/// <summary>所属角色目录的有界读取；Memory 与 PostgreSQL 限制实际读取范围。</summary>
public interface IRoleDirectory
{
    /// <summary>按编码或名称的不区分大小写字面子串读取最多 limit + 1 项。</summary>
    /// <param name="afterRoleId">排除的起点标识。</param>
    /// <param name="limit">页大小，1 至 100。</param>
    /// <param name="search">已经去除首尾空白的搜索词，空串表示不筛选。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>按标识有序的目录项。</returns>
    Task<IReadOnlyList<RoleDirectoryItem>> ReadAsync(long afterRoleId, int limit, string search, CancellationToken token);
}

/// <summary>有界角色查询；不读取用户归属或返回全体角色。</summary>
/// <param name="AfterRoleId">排除的起点标识，默认 0。</param>
/// <param name="Limit">页大小，默认 50，最大 100。</param>
/// <param name="Search">编码或名称子串，最多 64 字符。</param>
public sealed record GetRolesQuery(long AfterRoleId = 0, int Limit = 50, string? Search = null) : IQuery<RolePage>;

/// <summary>限制目录页大小与搜索范围。</summary>
/// <param name="directory">所属目录。</param>
public sealed class GetRolesHandler(IRoleDirectory directory) : IQueryHandler<GetRolesQuery, RolePage>
{
    /// <inheritdoc />
    public async Task<Result<RolePage>> HandleAsync(GetRolesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var search = query.Search?.Trim() ?? string.Empty;
        if (query.AfterRoleId < 0 || query.Limit is < 1 or > 100 || search.Length > RoleName.MaxLength)
        { return Result.Failure<RolePage>(new("identity.roles.page_invalid", "角色页大小必须为 1 至 100，游标不得小于 0，搜索词最多 64 字符。")); }
        var rows = await directory.ReadAsync(query.AfterRoleId, query.Limit, search, cancellationToken).ConfigureAwait(false);
        var items = rows.Take(query.Limit).ToArray();
        return Result.Success(new RolePage(items, rows.Count > query.Limit ? items[^1].RoleId : null));
    }
}
