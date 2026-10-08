using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure.Persistence;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>EF Core 用户仓储。只跟踪改动，由 Identity 命令事务统一保存。</summary>
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
    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        context.Users.Add(user);
        return Task.CompletedTask;
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
    public Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        context.Roles.Add(role);
        return Task.CompletedTask;
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
    public Task AddAsync(ApiResource resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        context.ApiResources.Add(resource);
        return Task.CompletedTask;
    }
}

/// <summary>
/// EF Core 菜单树仓储。
///
/// <para><b>节点是 <c>OwnsMany</c>，所以一次查询就把整棵树读了进来</b>——不需要 <c>Include</c>，
/// 也不该有"只读一层节点"的方法：菜单树是一个聚合（ADR-0001），
/// "移动一个节点必须同时改写它所有后代的物化路径"，半个树在内存里做不了这件事。</para>
///
/// <para>只跟踪新树；提交由 Identity 命令事务负责。</para>
/// </summary>
/// <param name="context">上下文。</param>
public sealed class EfMenuTreeRepository(IdentityDbContext context) : IMenuTreeRepository
{
    /// <inheritdoc />
    public async Task<MenuTree?> FindAsync(CancellationToken cancellationToken = default) =>
        await context.MenuTrees
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task AddAsync(MenuTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        context.MenuTrees.Add(tree);
        return Task.CompletedTask;
    }
}

/// <summary>把 Identity 的端口接到 EF Core 上。</summary>
public static class IdentityEntityFrameworkServiceCollectionExtensions
{
    /// <summary>Identity 所有的事实 Outbox。</summary>
    public const string OutboxKey = "identity";

    /// <summary>宿主的数据库启动检查与就绪探针；普通启动不执行迁移。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddIdentityDatabaseChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<IdentityDatabaseStartupCheck>();
        services.AddHealthChecks().AddCheck<IdentityDatabaseHealthCheck>("identity-database");
        return services;
    }

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

        // 用带 `IServiceProvider` 的重载：拦截器（审计字段 + 发件箱）从容器里取依赖。
        // 它们此前**写完了但没有任何注册点**——审计字段在生产里从不写、领域事件也不进发件箱，
        // 而"没写"与"没有要写的"从外面看是一样的。接在装配这一处，新增上下文不必记得它。
        services.AddScoped<IdentityCommittedFactInterceptor>();
        services.AddDbContext<IdentityDbContext>((provider, options) =>
            options
                .UseNexusStackPostgres(connectionString, IdentityDbContext.SchemaName)
                .UseNexusStackInterceptors(provider)
                .AddInterceptors(provider.GetRequiredService<IdentityCommittedFactInterceptor>()));

        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IUserDirectory, EfUserDirectory>();
        services.AddSingleton<ISessionStateReader>(provider => new PostgresSessionStateReader(connectionString,
            provider.GetService<IdentitySessionReadOptions>() ?? new()));
        services.AddScoped<IRoleRepository, EfRoleRepository>();
        services.AddScoped<IApiResourceRepository, EfApiResourceRepository>();
        services.AddScoped<IMenuTreeRepository, EfMenuTreeRepository>();
        services.AddScoped<IRefreshTokenRepository, EfRefreshTokenRepository>();

        // 工作单元与仓储同生命周期（都持有同一个上下文）。
        services.AddScoped<IIdentityUnitOfWork, EfIdentityUnitOfWork>();
        services.AddKeyedScoped<IOutboxStore, EfOutboxStore<IdentityDbContext>>(OutboxKey);
        services.AddScoped<IIdentityAuditDelivery, EfIdentityAuditDelivery>();

        services.AddScoped<UserPermissionReader>();

        services.AddIdentityTokenSecrets();

        // 口令哈希不是"存储"，但它与存储实现同属基础设施，且换算法时只改这一行。
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        return services;
    }
}

/// <summary>把通用 EF 事务实现绑定到 Identity 的工作单元端口。</summary>
/// <param name="context">Identity 的上下文。</param>
public sealed class EfIdentityUnitOfWork(IdentityDbContext context)
    : EfUnitOfWork<IdentityDbContext>(context), IIdentityUnitOfWork
{
    /// <inheritdoc />
    public new async Task<TResult> ExecuteInTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> operation,
        Func<TResult, bool>? shouldCommit = null, CancellationToken cancellationToken = default)
    {
        try { return await base.ExecuteInTransactionAsync(operation, shouldCommit, cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { throw new IdentityWriteConflictException(); }
    }
}
