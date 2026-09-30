using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Transactions;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>
/// 内存用户仓储。
/// <para>
/// <b>它保存的是聚合实例本身，不做拷贝。</b>这在开发与测试里是对的（同一个进程、同一个对象图），
/// 但它意味着外部改动聚合会直接反映到"存储"里——真实持久化不会有这个性质。
/// 这条差异写在这里，免得有人拿它当生产实现。
/// </para>
/// </summary>
public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<long, User> _users = new();

    /// <inheritdoc />
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return Task.FromResult(_users.TryGetValue(id.Value, out var user) ? user : null);
    }

    /// <inheritdoc />
    public Task<User?> FindByUserNameAsync(UserName userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        return Task.FromResult(_users.Values.FirstOrDefault(user => user.UserName.Equals(userName)));
    }

    /// <inheritdoc />
    public Task<bool> UserNameExistsAsync(UserName userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        return Task.FromResult(_users.Values.Any(existing => existing.UserName.Equals(userName)));
    }

    /// <inheritdoc />
    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        _users[user.Id.Value] = user;
        return Task.CompletedTask;
    }
}

/// <summary>内存角色仓储。</summary>
public sealed class InMemoryRoleRepository : IRoleRepository
{
    private readonly ConcurrentDictionary<long, Role> _roles = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<Role>> FindManyAsync(
        IEnumerable<RoleId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Select(static id => id.Value).ToHashSet();

        IReadOnlyList<Role> found = [.. _roles.Where(pair => wanted.Contains(pair.Key)).Select(static pair => pair.Value)];
        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<Role?> FindByCodeAsync(RoleCode code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        return Task.FromResult(_roles.Values.FirstOrDefault(role => role.Code.Equals(code)));
    }

    /// <inheritdoc />
    public Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        _roles[role.Id.Value] = role;
        return Task.CompletedTask;
    }
}

/// <summary>内存 API 资源仓储。</summary>
public sealed class InMemoryApiResourceRepository : IApiResourceRepository
{
    private readonly ConcurrentDictionary<long, ApiResource> _resources = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiResource>> FindByMenuIdsAsync(
        IReadOnlySet<MenuId> menuIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menuIds);

        IReadOnlyList<ApiResource> found =
        [
            .. _resources.Values
                .Where(resource => resource.MenuId is { } menuId && menuIds.Contains(menuId))
                .OrderBy(static resource => resource.Id.Value)
        ];

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task AddAsync(ApiResource resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        _resources[resource.Id.Value] = resource;
        return Task.CompletedTask;
    }
}

/// <summary>
/// 内存菜单树仓储。
///
/// <para><b>为什么必须注册成单例。</b>菜单树是单例聚合——整个上下文只有一棵。
/// 注册成 Scoped 会让每个请求看到自己的那棵空树，"建了菜单之后别人看不见"，
/// 而那种缺陷在内存存储下**只有跨请求才暴露**（本仓票据 54 踩过同形状的坑）。</para>
///
/// <para>与其它内存适配器一样，它保存的是聚合实例本身，不做拷贝——那条差异写在
/// <see cref="InMemoryUserRepository"/> 的文档里，这里不重复。</para>
/// </summary>
public sealed class InMemoryMenuTreeRepository : IMenuTreeRepository
{
    private MenuTree? _tree;

    /// <inheritdoc />
    public Task<MenuTree?> FindAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_tree);

    /// <inheritdoc />
    public Task AddAsync(MenuTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        _tree = tree;
        return Task.CompletedTask;
    }
}

/// <summary>把 Identity 的端口接到内存适配器上。</summary>
public static class IdentityInfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// 注册内存适配器与权限读取器。
    /// <para><b>显式注册，不做程序集扫描</b>（架构不变量 8）。</para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddIdentityInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IUserRepository, InMemoryUserRepository>();
        services.AddSingleton<IRoleRepository, InMemoryRoleRepository>();
        services.AddSingleton<IApiResourceRepository, InMemoryApiResourceRepository>();
        services.AddSingleton<IRefreshTokenRepository, InMemoryRefreshTokenRepository>();

        // 菜单树是单例聚合，所以适配器也必须是单例——理由写在 InMemoryMenuTreeRepository 上。
        services.AddSingleton<IMenuTreeRepository, InMemoryMenuTreeRepository>();

        // 秘密串的生成与哈希和"存在哪里"无关，所以两种存储都要注册。
        services.AddIdentityTokenSecrets();
        services.AddSingleton<UserPermissionReader>();

        // 内存存储**立刻生效**，因此没有可回滚的东西——但 `IUnitOfWork` 仍然必须存在：
        // 处理器依赖它，而"某个存储不提供它"会让同一段用例在不同存储下行为不同
        // （更糟的是：宿主会在 ValidateOnBuild 下起不来）。空实现是有意的，
        // 它的文档说清了"这里没有事务"这件事，而不是假装有。
        services.AddSingleton<IUnitOfWork, InMemoryUnitOfWork>();

        // 口令哈希不是"存储"，但它与存储实现同属基础设施，且换算法时只改这一行。
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        return services;
    }
}

/// <summary>
/// 内存存储的工作单元：**什么都不做**。
///
/// <para>内存适配器保存的是聚合实例本身，改动立刻可见——因此没有"提交"这个动作，
/// 也没有可回滚的东西。它不是"还没实现的占位符"，而是**对这份存储的正确实现**。</para>
///
/// <para>但它必须存在：处理器依赖 <see cref="NexusStackNext.BuildingBlocks.Application.Transactions.IUnitOfWork"/>，
/// 而"某个存储不提供它"会让宿主在 <c>ValidateOnBuild</c> 下直接起不来——
/// 那正是本仓票据 54 踩过的形状。</para>
/// </summary>
public sealed class InMemoryUnitOfWork : IUnitOfWork
{
    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <inheritdoc />
    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // 没有事务可开：内存存储的每次改动都立刻生效。
        return operation(cancellationToken);
    }
}
