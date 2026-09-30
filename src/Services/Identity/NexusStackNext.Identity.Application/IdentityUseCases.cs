using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Application;

// ---------------------------------------------------------------------------
// 请求
//
// 它们住在 Application 而不是端点层：用例的输入形状由**用例**决定，
// 而端点只负责把它从 HTTP 里解出来。放在端点层会让"换个入口（gRPC、后台任务）
// 就要重写一遍用例"——那是把 HTTP 的形状泄漏进了业务。
// ---------------------------------------------------------------------------

/// <summary>创建用户。返回新用户的标识。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="Password">明文口令——<b>只在这一次调用里存在</b>，进领域前已被换成哈希。</param>
public sealed record CreateUserCommand(string UserName, string Password) : ICommand<long>;

/// <summary>给用户分配角色。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="RoleId">角色标识。</param>
public sealed record AssignRoleCommand(long UserId, long RoleId) : ICommand, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId, RoleId];
}

/// <summary>查询某个用户的有效权限键。</summary>
/// <param name="UserId">用户标识。</param>
public sealed record GetUserPermissionsQuery(long UserId) : IQuery<IReadOnlyList<string>>, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>创建角色。返回新角色的标识。</summary>
/// <param name="Code">角色编码。</param>
/// <param name="Name">角色名称。</param>
public sealed record CreateRoleCommand(string Code, string Name) : ICommand<long>;

/// <summary>给角色授予一个菜单。</summary>
/// <param name="RoleId">角色标识。</param>
/// <param name="MenuId">菜单标识。</param>
public sealed record GrantMenuToRoleCommand(long RoleId, long MenuId) : ICommand, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [RoleId, MenuId];
}

/// <summary>注册一个 API 资源。</summary>
/// <param name="Path">路由模板。</param>
/// <param name="Method">HTTP 方法。</param>
/// <param name="MenuId">所属菜单；<c>null</c> 表示不对应菜单。</param>
public sealed record CreateApiResourceCommand(string Path, string Method, long? MenuId)
    : ICommand<ApiResourceCreated>;

/// <summary>新建 API 资源的结果。</summary>
/// <param name="ApiResourceId">资源标识。</param>
/// <param name="PermissionKey">由它产生的权限键。</param>
public sealed record ApiResourceCreated(long ApiResourceId, string PermissionKey);

/// <summary>判定某个用户能否执行某个请求。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="Path">请求路径。</param>
/// <param name="Method">HTTP 方法。</param>
public sealed record AuthorizeQuery(long UserId, string Path, string Method) : IQuery<AuthorizationOutcome>, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>一次授权判定的结果。</summary>
/// <param name="RequiredKey">判定所依据的权限键。</param>
/// <param name="Decision">判定结论。</param>
/// <param name="GrantedCount">该用户被授予的权限键数量——诊断用。</param>
public sealed record AuthorizationOutcome(string RequiredKey, string Decision, int GrantedCount);

// ---------------------------------------------------------------------------
// 处理器
//
// **它们只改聚合，不保存。** 分发器（`Sender`）在处理器成功之后
// 自动 `ExecuteInTransactionAsync` + `SaveChangesAsync`（见 `Sender.cs`）——
// 于是"每个命令一个事务"（不变量 4）由一处保证，而不是每个处理器各自记得。
//
// 端点此前是内联的，而且**不调用 SaveChanges**：内存存储下看不出问题
// （内存版保存的是聚合实例本身），换成 EF 就会让角色分配、菜单授权**静默不落库**
// 而接口照返回 204。走分发器之后这条路径不再依赖任何人记得。
// ---------------------------------------------------------------------------

/// <summary>创建用户。</summary>
/// <param name="users">用户仓储。</param>
/// <param name="hasher">口令哈希。</param>
/// <param name="ids">标识生成。</param>
/// <param name="clock">时钟。</param>
public sealed class CreateUserHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CreateUserCommand, long>
{
    /// <inheritdoc />
    public async Task<Result<long>> HandleAsync(
        CreateUserCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var userName = UserName.Create(command.UserName);
        if (userName.IsFailure)
        {
            return Result.Failure<long>(userName.Error);
        }

        // 由仓储回答"重名了吗"，而不是"先查一遍再插入"——那之间有竞态。
        if (await users.UserNameExistsAsync(userName.Value, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<long>(new Error(
                "identity.user_name.taken",
                $"用户名已被占用：{userName.Value.Value}。"));
        }

        var passwordHash = PasswordHash.Create(hasher.Hash(command.Password));
        if (passwordHash.IsFailure)
        {
            return Result.Failure<long>(passwordHash.Error);
        }

        var user = User.Register(new UserId(ids.NextId()), userName.Value, passwordHash.Value, clock.UtcNow);
        await users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        return Result.Success(user.Id.Value);
    }
}

/// <summary>给用户分配角色。</summary>
/// <param name="users">用户仓储。</param>
/// <param name="clock">时钟。</param>
public sealed class AssignRoleHandler(
    IUserRepository users,
    IClock clock) : ICommandHandler<AssignRoleCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        AssignRoleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await users.FindAsync(new UserId(command.UserId), cancellationToken).ConfigureAwait(false);

        return user is null
            ? Result.Failure(new Error("identity.user.not_found", $"用户不存在：{command.UserId}。"))
            : user.AssignRole(new RoleId(command.RoleId), clock.UtcNow);
    }
}

/// <summary>查询用户的有效权限键。</summary>
/// <param name="cache">权限缓存。</param>
public sealed class GetUserPermissionsHandler(IPermissionCache cache)
    : IQueryHandler<GetUserPermissionsQuery, IReadOnlyList<string>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> HandleAsync(
        GetUserPermissionsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var permissions = await cache.GetAsync(new UserId(query.UserId), cancellationToken).ConfigureAwait(false);

        return permissions.IsFailure
            ? Result.Failure<IReadOnlyList<string>>(permissions.Error)
            : Result.Success(permissions.Value.Values);
    }
}

/// <summary>创建角色。</summary>
/// <param name="roles">角色仓储。</param>
/// <param name="ids">标识生成。</param>
public sealed class CreateRoleHandler(
    IRoleRepository roles,
    IIdGenerator ids) : ICommandHandler<CreateRoleCommand, long>
{
    /// <inheritdoc />
    public async Task<Result<long>> HandleAsync(
        CreateRoleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var code = RoleCode.Create(command.Code);
        if (code.IsFailure)
        {
            return Result.Failure<long>(code.Error);
        }

        if (await roles.FindByCodeAsync(code.Value, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Result.Failure<long>(new Error(
                "identity.role_code.taken",
                $"角色编码已存在：{code.Value.Value}。"));
        }

        var name = RoleName.Create(command.Name);
        if (name.IsFailure)
        {
            return Result.Failure<long>(name.Error);
        }

        var role = Role.Create(new RoleId(ids.NextId()), code.Value, name.Value);
        await roles.AddAsync(role, cancellationToken).ConfigureAwait(false);

        return Result.Success(role.Id.Value);
    }
}

/// <summary>给角色授予一个菜单。</summary>
/// <param name="roles">角色仓储。</param>
/// <param name="clock">时钟。</param>
/// <param name="permissions">权限缓存——授权变了，缓存必须失效。</param>
public sealed class GrantMenuToRoleHandler(
    IRoleRepository roles,
    IClock clock,
    IPermissionCache permissions) : ICommandHandler<GrantMenuToRoleCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        GrantMenuToRoleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await roles.FindManyAsync([new RoleId(command.RoleId)], cancellationToken).ConfigureAwait(false);

        if (found.Count == 0)
        {
            return Result.Failure(new Error("identity.role.not_found", $"角色不存在：{command.RoleId}。"));
        }

        var result = found[0].Grant(new MenuId(command.MenuId), clock.UtcNow);

        if (result.IsSuccess)
        {
            // **授权变了，缓存必须失效。** 而且要在命令成功之后、且在同一个用例里——
            // 把它留给调用方，就会出现"某条路径忘了失效"，而那正是参照仓库 10 小时窗口的成因。
            permissions.Invalidate();
        }

        return result;
    }
}

/// <summary>注册 API 资源。</summary>
/// <param name="resources">资源仓储。</param>
/// <param name="ids">标识生成。</param>
/// <param name="permissions">权限缓存——新增资源会改变所有拥有该菜单的人的有效权限。</param>
public sealed class CreateApiResourceHandler(
    IApiResourceRepository resources,
    IIdGenerator ids,
    IPermissionCache permissions) : ICommandHandler<CreateApiResourceCommand, ApiResourceCreated>
{
    /// <inheritdoc />
    public async Task<Result<ApiResourceCreated>> HandleAsync(
        CreateApiResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var pattern = RoutePattern.Create(command.Path);
        if (pattern.IsFailure)
        {
            return Result.Failure<ApiResourceCreated>(pattern.Error);
        }

        var resource = ApiResource.Create(
            new ApiResourceId(ids.NextId()),
            pattern.Value,
            command.Method,
            command.MenuId is { } menuId ? new MenuId(menuId) : null);

        if (resource.IsFailure)
        {
            return Result.Failure<ApiResourceCreated>(resource.Error);
        }

        await resources.AddAsync(resource.Value, cancellationToken).ConfigureAwait(false);

        permissions.Invalidate();

        return Result.Success(new ApiResourceCreated(
            resource.Value.Id.Value,
            resource.Value.PermissionKey.Value));
    }
}

/// <summary>判定某个用户能否执行某个请求。</summary>
/// <param name="cache">权限缓存。</param>
public sealed class AuthorizeHandler(IPermissionCache cache)
    : IQueryHandler<AuthorizeQuery, AuthorizationOutcome>
{
    /// <inheritdoc />
    public async Task<Result<AuthorizationOutcome>> HandleAsync(
        AuthorizeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var permissions = await cache.GetAsync(new UserId(query.UserId), cancellationToken).ConfigureAwait(false);

        if (permissions.IsFailure)
        {
            return Result.Failure<AuthorizationOutcome>(permissions.Error);
        }

        var required = PermissionKey.From(query.Path, query.Method);

        // 刻意走**真实的**判定核心（票据 22），而不是在处理器里重写一遍规则。
        // 没有认证形态（尚未确定）之前，isAuthenticated 与 isRoot 只能是占位——
        // 但"判定本身"是真实的：它吃的是刚投影出来的权限键集合。
        var decision = AccessPolicy.Decide(
            isAuthenticated: true,
            isRoot: false,
            granted: permissions.Value,
            required: required,
            mode: AuthorizationMode.PermissionKey);

        return Result.Success(new AuthorizationOutcome(
            required.Value,
            decision.ToString(),
            permissions.Value.Count));
    }
}

/// <summary>注册 Identity 的用例处理器。<b>显式注册，不做程序集扫描</b>（不变量 8）。</summary>
public static class IdentityUseCaseServiceCollectionExtensions
{
    /// <summary>注册命令与查询处理器。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddIdentityUseCases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ICommandHandler<CreateUserCommand, long>, CreateUserHandler>();
        services.AddScoped<ICommandHandler<LoginCommand, LoginOutcome>, LoginHandler>();
        services.AddScoped<ICommandHandler<RefreshTokenCommand, TokenPair>, RefreshTokenHandler>();
        services.AddScoped<ICommandHandler<LogoutCommand>, LogoutHandler>();

        // 令牌签发与轮换。它不是"基础设施"——里面全是策略（轮换、重放检测、撤销整条链）。
        services.AddScoped<TokenIssuer>();

        // 会话版本：签发时读、过滤器比对。**Singleton**——它必须在整个进程里是同一份，
        // 否则"撤销"只对某个作用域生效。
        services.AddSingleton<ISessionVersionStore, InMemorySessionVersionStore>();
        services.AddScoped<ICommandHandler<AssignRoleCommand>, AssignRoleHandler>();
        services.AddScoped<ICommandHandler<CreateRoleCommand, long>, CreateRoleHandler>();
        services.AddScoped<ICommandHandler<GrantMenuToRoleCommand>, GrantMenuToRoleHandler>();
        services.AddScoped<ICommandHandler<CreateApiResourceCommand, ApiResourceCreated>, CreateApiResourceHandler>();

        services.AddScoped<IQueryHandler<GetUserPermissionsQuery, IReadOnlyList<string>>, GetUserPermissionsHandler>();
        services.AddScoped<IQueryHandler<AuthorizeQuery, AuthorizationOutcome>, AuthorizeHandler>();

        // ---------- 验证码 ----------
        //
        // **默认实现的名字就是警告**：`NoCaptchaValidation` 不做任何校验。
        // 生产环境必须换成真的——参照仓库写了 `ValidateCaptchaAsync` 却零调用，
        // 而"有一个没人调用的校验"与"没有校验"在安全上是同一件事。
        services.AddSingleton<ICaptchaValidation, NoCaptchaValidation>();

        // ---------- 请求校验 ----------
        //
        // 分发器在**开启事务之前**调用它们：一条不合法的请求不该占用一个数据库连接。
        services.AddSingleton<IRequestValidator<CreateUserCommand>, CreateUserCommandValidator>();

        // 一个校验器覆盖所有带标识的请求——靠的是 IRequestValidator<in TRequest> 的逆变。
        services.AddSingleton<IRequestValidator<IIdentifiedRequest>, IdentifiedRequestValidator>();

        // ---------- 权限预计算缓存 ----------
        //
        // 读取器由存储注册按自己的生存期给出（内存版 Singleton、EF 版 Scoped），
        // 而缓存**必须**跨请求存在才有意义（Singleton）——直接把 Scoped 的读取器
        // 注入 Singleton 缓存是捕获依赖，`ValidateScopes` 会当场拒绝。
        //
        // 桥接放在 `ScopedPermissionSource` 里（它自己按调用开作用域），
        // 而不是让缓存去管作用域——那样缓存就要认识 DI，而它本该只认识端口。
        services.AddSingleton<IPermissionSource, ScopedPermissionSource>();
        services.AddSingleton<IPermissionCache, UserPermissionCache>();

        // 授权过滤器用的端口。它住在 Identity 里，因为权限缓存与用户表都属于这个上下文。
        services.AddSingleton<IPermissionChecker, CachedPermissionChecker>();

        return services;
    }
}

/// <summary>
/// 把 <see cref="UserPermissionReader"/>（按存储的生存期）桥接成缓存能长期持有的单例端口。
///
/// <para><b>为什么需要它。</b>缓存跨请求存在（Singleton），而权限读取要读数据库——
/// EF 的 <c>DbContext</c> 是 Scoped，把 Scoped 塞进 Singleton 是**捕获依赖**：
/// 第一个请求的上下文会被后续所有请求共用，而 EF 的上下文不是线程安全的。
/// 那种缺陷不会立刻报错，它会在并发的某一次上以"另一个操作正在进行"或脏读的形式出现。</para>
///
/// <para>桥接放在这里而不是缓存里：缓存本该只认识端口，不认识 DI。
/// 每次回源开一个作用域、用完就还——作用域的开销相对于一次数据库往返可以忽略。</para>
/// </summary>
/// <param name="scopeFactory">作用域工厂。</param>
public sealed class ScopedPermissionSource(IServiceScopeFactory scopeFactory) : IPermissionSource
{
    /// <inheritdoc />
    public async Task<Result<PermissionKeySet>> ReadAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        await using var scope = scopeFactory.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<UserPermissionReader>()
            .ReadAsync(userId, cancellationToken)
            .ConfigureAwait(false);
    }
}
