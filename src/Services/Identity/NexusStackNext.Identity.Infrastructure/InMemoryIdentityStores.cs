using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>开发用用户仓储；工作副本由同作用域的 Identity 工作单元提交。</summary>
public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly IdentityMemorySession _session;
    internal InMemoryUserRepository(IdentityMemorySession session) => _session = session;

    /// <inheritdoc />
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return Task.FromResult(_session.Data(cancellationToken).Users.GetValueOrDefault(id));
    }

    /// <inheritdoc />
    public Task<User?> FindByUserNameAsync(UserName userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        return Task.FromResult(_session.Data(cancellationToken).Users.Values.FirstOrDefault(user => user.UserName.Equals(userName)));
    }

    /// <inheritdoc />
    public Task<bool> UserNameExistsAsync(UserName userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        return Task.FromResult(_session.Data(cancellationToken).Users.Values.Any(existing => existing.UserName.Equals(userName)));
    }

    /// <inheritdoc />
    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        _session.Data(cancellationToken).Users.Add(user.Id, user);
        return Task.CompletedTask;
    }
}

/// <summary>内存角色仓储。</summary>
public sealed class InMemoryRoleRepository : IRoleRepository
{
    private readonly IdentityMemorySession _session;
    internal InMemoryRoleRepository(IdentityMemorySession session) => _session = session;

    /// <inheritdoc />
    public Task<IReadOnlyList<Role>> FindManyAsync(
        IEnumerable<RoleId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Select(static id => id.Value).ToHashSet();

        IReadOnlyList<Role> found = [.. _session.Data(cancellationToken).Roles.Values.Where(role => wanted.Contains(role.Id.Value)).OrderBy(role => role.Id.Value)];
        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<Role?> FindByCodeAsync(RoleCode code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        return Task.FromResult(_session.Data(cancellationToken).Roles.Values.FirstOrDefault(role => role.Code.Equals(code)));
    }

    /// <inheritdoc />
    public Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        _session.Data(cancellationToken).Roles.Add(role.Id, role);
        return Task.CompletedTask;
    }
}

/// <summary>内存 API 资源仓储。</summary>
public sealed class InMemoryApiResourceRepository : IApiResourceRepository
{
    private readonly IdentityMemorySession _session;
    internal InMemoryApiResourceRepository(IdentityMemorySession session) => _session = session;

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiResource>> FindByMenuIdsAsync(
        IReadOnlySet<MenuId> menuIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menuIds);

        IReadOnlyList<ApiResource> found =
        [
            .. _session.Data(cancellationToken).Resources.Values
                .Where(resource => resource.MenuId is { } menuId && menuIds.Contains(menuId))
                .OrderBy(static resource => resource.Id.Value)
        ];

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task AddAsync(ApiResource resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        _session.Data(cancellationToken).Resources.Add(resource.Id, resource);
        return Task.CompletedTask;
    }
}

/// <summary>开发用菜单树仓储；共享已提交状态，每个作用域独立跟踪树与节点。</summary>
public sealed class InMemoryMenuTreeRepository : IMenuTreeRepository
{
    private readonly IdentityMemorySession _session;
    internal InMemoryMenuTreeRepository(IdentityMemorySession session) => _session = session;

    /// <inheritdoc />
    public Task<MenuTree?> FindAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_session.Data(cancellationToken).Trees.Values.SingleOrDefault());

    /// <inheritdoc />
    public Task AddAsync(MenuTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        _session.Data(cancellationToken).Trees.Add(tree.Id, tree);
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
    /// <param name="capacity">显式开发存储的事实保留上限。</param>
    /// <param name="write">每次共用写锁获取的等待预算。</param>
    /// <param name="control">所属 Memory 控制池的启动上限。</param>
    /// <param name="recovery">独立恢复凭据池的启动上限。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddIdentityInMemoryStorage(this IServiceCollection services, MemoryCommittedFactCapacityOptions? capacity = null,
        CommittedFactCapacityWriteOptions? write = null, MemoryFactCapacityPolicyControlOptions? control = null,
        MemoryFactDeliveryRecoveryControlOptions? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var controlLimits = control ?? new();
        controlLimits.Validate();
        var recoveryLimits = recovery ?? new();
        recoveryLimits.Validate();
        services.AddSingleton(new IdentityMemoryState(capacity, write));
        services.AddSingleton<ISessionStateReader>(provider => new MemorySessionStateReader(provider.GetRequiredService<IdentityMemoryState>(),
            provider.GetService<IdentitySessionReadOptions>() ?? new()));
        services.AddSingleton<IIdentityAuditDelivery>(provider => new InMemoryIdentityAuditDelivery(
            provider.GetRequiredService<IdentityMemoryState>(), recoveryLimits));
        services.AddKeyedSingleton<InMemoryFactCapacityPolicyStore>("identity", (provider, _) =>
        {
            var state = provider.GetRequiredService<IdentityMemoryState>();
            return new(state.Gate, state.Capacity, () => state.Outbox, prepared => state.Outbox = prepared,
                new("identity", IdentityFactCapacityPolicyChangedV1.From), provider.GetRequiredService<IIntegrationEventSerializer>(), controlLimits);
        });
        services.AddKeyedSingleton<ICommittedFactCapacityPolicyStore>("identity", (provider, _) => provider.GetRequiredKeyedService<InMemoryFactCapacityPolicyStore>("identity"));
        services.AddKeyedSingleton<ICommittedFactCapacityPolicyCleanup>("identity", (provider, _) => provider.GetRequiredKeyedService<InMemoryFactCapacityPolicyStore>("identity"));
        services.AddKeyedSingleton<ICommittedFactCapacityReader>("identity", (provider, _) => provider.GetRequiredService<IdentityMemoryState>());
        services.AddScoped<IdentityMemorySession>();
        services.AddScoped<IdentityMemoryFacts>();
        services.AddKeyedSingleton<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey,
            (provider, _) => new IdentityMemoryOutbox(provider.GetRequiredService<IdentityMemoryState>()));
        services.AddScoped<IUserRepository>(provider => new InMemoryUserRepository(provider.GetRequiredService<IdentityMemorySession>()));
        services.AddScoped<IRoleRepository>(provider => new InMemoryRoleRepository(provider.GetRequiredService<IdentityMemorySession>()));
        services.AddScoped<IApiResourceRepository>(provider => new InMemoryApiResourceRepository(provider.GetRequiredService<IdentityMemorySession>()));
        services.AddScoped<IRefreshTokenRepository>(provider => new InMemoryRefreshTokenRepository(provider.GetRequiredService<IdentityMemorySession>()));
        services.AddScoped<IMenuTreeRepository>(provider => new InMemoryMenuTreeRepository(provider.GetRequiredService<IdentityMemorySession>()));

        // 秘密串的生成与哈希和"存在哪里"无关，所以两种存储都要注册。
        services.AddIdentityTokenSecrets();
        services.AddScoped<UserPermissionReader>();

        services.AddScoped<IIdentityUnitOfWork>(provider => new InMemoryUnitOfWork(provider.GetRequiredService<IdentityMemorySession>()));

        // 口令哈希不是"存储"，但它与存储实现同属基础设施，且换算法时只改这一行。
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        return services;
    }
}

/// <summary>开发用工作单元；保存暂存快照，事务成功结束后才向其他作用域发布。</summary>
public sealed class InMemoryUnitOfWork : IIdentityUnitOfWork
{
    private readonly IdentityMemorySession _session;
    internal InMemoryUnitOfWork(IdentityMemorySession session) => _session = session;
    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => _session.SaveAsync(cancellationToken);

    /// <inheritdoc />
    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        Func<TResult, bool>? shouldCommit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return _session.ExecuteAsync(operation, shouldCommit, cancellationToken);
    }
}
