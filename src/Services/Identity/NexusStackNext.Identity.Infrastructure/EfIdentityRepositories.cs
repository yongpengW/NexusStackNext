using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Transactions;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure.Persistence;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>
/// EF Core 用户仓储。
///
/// <para><b>与内存版有一处必须说清的语义差异。</b>内存版保存的是**聚合实例本身**
/// （它的文档里已写明这一点），因此外部改动会直接反映到"存储"里；
/// EF 版不是——改动要经 <c>SaveChanges</c> 才落库。
/// 也就是说：<b>读取 → 改 → 保存</b>这条路径需要调用方经 <c>IUnitOfWork.SaveChangesAsync</c>，
/// 而 <c>AddAsync</c> 自己会保存（见下）。</para>
///
/// <para><b>为什么 <c>AddAsync</c> 里直接保存。</b>端口说的是"保存新用户"，而内存版就是立刻生效的。
/// 让 EF 版只 <c>Add</c> 不保存，会让同一段用例在两个实现下行为不同——
/// 而"换个适配器行为就变了"正是端口最该避免的事。多一次 <c>SaveChanges</c> 在
/// <c>IUnitOfWork.ExecuteInTransactionAsync</c> 里也只占同一个事务，不会多开。</para>
/// </summary>
/// <param name="context">上下文。</param>
public sealed class EfUserRepository(IdentityDbContext context) : IUserRepository
{
    /// <inheritdoc />
    public async Task<User?> FindAsync(UserId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return await context.Users
            .FirstOrDefaultAsync(user => user.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> FindByUserNameAsync(UserName userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        return await context.Users
            .FirstOrDefaultAsync(user => user.UserName == userName, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> UserNameExistsAsync(UserName userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        // 交给数据库回答，而不是"查一遍看看"——后者有竞态。
        // 真正的兜底仍然是唯一索引（ux_users_user_name），这里只是把常见路径走快。
        return context.Users.AnyAsync(user => user.UserName == userName, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core 角色仓储。</summary>
/// <param name="context">上下文。</param>
public sealed class EfRoleRepository(IdentityDbContext context) : IRoleRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Role>> FindManyAsync(
        IEnumerable<RoleId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.ToList();

        // 找不到的标识被忽略——端口就是这么写的（`FindMany` 而不是 `GetMany`）。
        return await context.Roles
            .Where(role => wanted.Contains(role.Id))
            .OrderBy(role => role.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Role?> FindByCodeAsync(RoleCode code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        return await context.Roles
            .FirstOrDefaultAsync(role => role.Code == code, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        context.Roles.Add(role);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core API 资源仓储。</summary>
/// <param name="context">上下文。</param>
public sealed class EfApiResourceRepository(IdentityDbContext context) : IApiResourceRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiResource>> FindByMenuIdsAsync(
        IReadOnlySet<MenuId> menuIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menuIds);

        if (menuIds.Count == 0)
        {
            return [];
        }

        var wanted = menuIds.ToList();

        return await context.ApiResources
            .Where(resource => resource.MenuId != null && wanted.Contains(resource.MenuId))
            .OrderBy(resource => resource.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(ApiResource resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        context.ApiResources.Add(resource);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>把 Identity 的端口接到 EF Core 上。</summary>
public static class IdentityEntityFrameworkServiceCollectionExtensions
{
    /// <summary>
    /// 注册 EF Core 适配器。
    ///
    /// <para><b>内存版仍然保留</b>（<c>AddIdentityInMemoryStorage</c>）：测试与无库开发需要它。
    /// 两者是**同一个端口的两个适配器**，不是"临时实现与正式实现"——
    /// 这份对称正是端口存在意义的一部分。</para>
    ///
    /// <para>调用方自己选一个注册，**不要两个都注册**：同一端口注册两次时，
    /// 最后注册的胜出——而"哪个胜出"取决于代码顺序，那不是一条能读出来的规则。</para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">连接串。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddIdentityEntityFrameworkStorage(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<IdentityDbContext>(options =>
            options.UseNexusStackPostgres(connectionString, IdentityDbContext.SchemaName));

        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IRoleRepository, EfRoleRepository>();
        services.AddScoped<IApiResourceRepository, EfApiResourceRepository>();
        services.AddScoped<IRefreshTokenRepository, EfRefreshTokenRepository>();

        // 工作单元与仓储同生命周期（都持有同一个上下文）。
        services.AddScoped<IUnitOfWork, EfUnitOfWork<IdentityDbContext>>();

        services.AddScoped<UserPermissionReader>();

        services.AddIdentityTokenSecrets();

        // 口令哈希不是"存储"，但它与存储实现同属基础设施，且换算法时只改这一行。
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        return services;
    }
}
