using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Infrastructure;

namespace NexusStackNext.Identity.Endpoints;

/// <summary>
/// Identity 模块：本上下文对宿主暴露的全部内容——DI 注册与 HTTP 端点。
///
/// <para>它在边缘之后提供"谁是谁、谁能做什么"：
/// 用户 → 角色 → 菜单 → 端点 → 权限键 → 判定。</para>
///
/// <para><b>端点只做三件事</b>：从 HTTP 里解出请求、交给分发器、把结果映射成状态码。
/// 业务逻辑在 <c>NexusStackNext.Identity.Application</c> 的处理器里——
/// 它此前是内联在这里的，代价见下。</para>
///
/// <para><b>为什么必须走分发器，而不只是"更整齐"。</b>内联版本**不调用 <c>SaveChanges</c>**：
/// 内存存储下看不出问题（内存版保存的是聚合实例本身），但换成 EF 之后，
/// 角色分配、菜单授权会**静默地不落库**，而接口照返回 204。
/// 分发器交给已经装饰的 Identity 命令入口，后者负责开启事务、执行处理器及保存提交
/// （见 <c>IdentityCommandTransaction</c>），端点不自行管理事务。</para>
///
/// <para>模块边界见 <c>NexusStackNext.Auditing.Endpoints.AuditingModule</c> 的说明（ADR-0013）。</para>
/// </summary>
public static class IdentityModule
{
    /// <summary>注册本模块需要的服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置——JWT 签名密钥从它读，**不进仓库**（ADR-0014）。</param>
    /// <param name="environment">运行环境；内存存储只允许开发与测试。</param>
    /// <returns>同一个服务集合，便于串联。</returns>
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        ArgumentNullException.ThrowIfNull(environment);
        var provider = configuration["Identity:Storage:Provider"];
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = "Postgres";
        }
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Identity:Storage:Provider=Memory 仅允许 Development / Testing 环境。");
            }

            services.AddIdentityInMemoryStorage();
        }
        else if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connection = configuration.GetConnectionString("Identity");
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new InvalidOperationException("必须配置 ConnectionStrings:Identity；开发测试可显式选择 Identity:Storage:Provider=Memory。");
            }

            services.AddIdentityEntityFrameworkStorage(connection);
            services.AddIdentityDatabaseChecks();
        }
        else
        {
            throw new InvalidOperationException("Identity:Storage:Provider 仅支持 Postgres / Memory。");
        }

        // 签名密钥的取值顺序由 `AddNexusStackAgileConfig` 定：环境变量 > 配置中心 > appsettings。
        // **它绝不该出现在仓库里**——那些文件是模板的一部分。
        services.AddIdentityJwtIssuer(configuration);

        // 用例处理器与"存储是哪种"无关，所以注册在存储之后、且不随之切换。
        services.AddIdentityUseCases();

        // **根账号播种**（票据 71 的决定）：每次启动跑一次，已有同名账号则跳过。
        // 它在这里注册而不是在各宿主里：这是 Identity 自己的引导，五个宿主不该各写一遍。
        services.AddHostedService<RootAccountSeeder>();

        return services;
    }

    /// <summary>映射本模块的端点。</summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var identity = endpoints.MapGroup("/api/identity").ProducesApiErrors(400, 401, 403, 500);

        // **授权过滤器挂在整个分组上**，于是"这个模块的端点默认都要过一遍授权"是结构性的，
        // 而不是每个端点各自的记性。公开的端点由框架的 `AllowAnonymous()` 显式标注。
        identity.AddEndpointFilter<NexusStackAuthorizationFilter>();

        // 自述端点：说明这个服务是什么。**不返回任何假数据。**
        identity.MapGet("/", (ApiResponses responses, IClock clock) => responses.Ok(new
        {
            context = "identity",
            responsibility = "谁可以登录、登录后能做什么",
            capabilities = new { users = true, roles = true, permissions = true, authorization = true, tokens = false },
            at = clock.UtcNow,
        })).AllowAnonymous();
        // 自述端点必须公开：它是"这个服务是什么"的说明书，而说明书不该要钥匙。

        // ---------- 认证 ----------
        //
        // 登录与刷新放在最前面：它们是这个上下文**唯一一对不需要先有身份就能调用的端点**，
        // 而其余所有端点都假定调用方已经是谁。
        //
        // **响应里带着刷新令牌的原文，而它只在这里出现一次。**
        // 它不该进日志——所以这两个端点不要挂请求体记录类的东西。

        identity.MapPost("/login", async (ApiResponses responses,
            LoginRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(
                new LoginCommand(request.UserName, request.Password, request.Captcha),
                cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Ok(new LoginResponse(result.Value.UserId, result.Value.UserName,
                    result.Value.Tokens.AccessToken, result.Value.Tokens.AccessTokenExpiresAt,
                    result.Value.Tokens.RefreshToken, result.Value.Tokens.RefreshTokenExpiresAt));
        }).Produces<ApiResponse<LoginResponse>>().AllowAnonymous();   // 登录当然要公开——它是拿钥匙的地方。

        identity.MapPost("/refresh", async (ApiResponses responses,
            RefreshRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(
                new RefreshTokenCommand(request.RefreshToken),
                cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Ok(new RefreshResponse(result.Value.AccessToken, result.Value.AccessTokenExpiresAt,
                    result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt));
        }).Produces<ApiResponse<RefreshResponse>>().AllowAnonymous();   // 刷新也一样：访问令牌过期时，客户端手里只有刷新令牌。

        identity.MapPost("/logout", async (
            ISender sender,
            ICurrentUser currentUser,
            CancellationToken cancellationToken) =>
        {
            // **用户标识从令牌来，不从请求体来。** 让请求体指定"注销谁"，
            // 等于给了一个注销任何人的接口。
            if (currentUser.UserId is null
                || !long.TryParse(currentUser.UserId, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var userId))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "需要先认证。");
            }

            var result = await sender.SendAsync(new LogoutCommand(userId), cancellationToken);

            return result.IsFailure ? Failure(result.Error) : Results.NoContent();
        }).Produces(204).RequireAuthenticated();

        // ---------- 用户 ----------

        identity.MapPost("/users", async (ApiResponses responses,
            CreateUserRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(
                new CreateUserCommand(request.UserName, request.Password),
                cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Created($"/api/identity/users/{result.Value}", new UserCreatedResponse(result.Value));
        }).Produces<ApiResponse<UserCreatedResponse>>(201).ProducesApiErrors(409)
        // **自注册公开，是显式的。**
        //
        // 它必须是公开的，否则没有人能创建第一个用户——而"发一个令牌"需要先有用户。
        // 这是**产品决定**而不是遗漏：模板默认允许自注册，生产环境若要关掉，
        // 应当把它改成 `RequirePermission` 并配上种子管理员，而不是靠"忘了标注"来挡住。
        .AllowAnonymous();

        identity.MapPost("/users/{userId:long}/roles/{roleId:long}", async (
            long userId,
            long roleId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(new AssignRoleCommand(userId, roleId), cancellationToken);

            return result.IsFailure ? Failure(result.Error) : Results.NoContent();
        }).Produces(204).ProducesApiErrors(404).RequirePermission("/api/identity/users/{userId}/roles/{roleId}", "POST");

        identity.MapGet("/users/{userId:long}/permissions", async (ApiResponses responses,
            long userId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.QueryAsync(new GetUserPermissionsQuery(userId), cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Ok(new UserPermissionsResponse(userId, result.Value));
        }).Produces<ApiResponse<UserPermissionsResponse>>().ProducesApiErrors(404).RequirePermission("/api/identity/users/{userId}/permissions", "GET");

        // ---------- 角色 ----------

        identity.MapPost("/roles", async (ApiResponses responses,
            CreateRoleRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(new CreateRoleCommand(request.Code, request.Name), cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Created($"/api/identity/roles/{result.Value}", new RoleCreatedResponse(result.Value));
        }).Produces<ApiResponse<RoleCreatedResponse>>(201).ProducesApiErrors(409).RequirePermission("/api/identity/roles", "POST");

        identity.MapPost("/roles/{roleId:long}/menus/{menuId:long}", async (
            long roleId,
            long menuId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(new GrantMenuToRoleCommand(roleId, menuId), cancellationToken);

            return result.IsFailure ? Failure(result.Error) : Results.NoContent();
        }).Produces(204).ProducesApiErrors(404).RequirePermission("/api/identity/roles/{roleId}/menus/{menuId}", "POST");

        // ---------- API 资源（权限键的来源） ----------

        identity.MapPost("/api-resources", async (ApiResponses responses,
            CreateApiResourceRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(
                new CreateApiResourceCommand(request.Path, request.Method, request.MenuId),
                cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Created(
                    $"/api/identity/api-resources/{result.Value.ApiResourceId}",
                    result.Value);
        }).Produces<ApiResponse<ApiResourceCreated>>(201)
        // **引导端点：只要求"已认证"，不要求权限键。**
        //
        // 这里有一个真实的循环：要授权得先有权限键，而权限键由这个端点登记。
        // 要求"注册权限"本身需要权限，就没人能注册第一个——系统永远起不来。
        // 所以它停在"已认证"这一档。**代价说清楚**：任何已认证用户都能登记 API 资源，
        // 而那意味着他能给自己造权限。生产部署必须把这个端点限制住（网关侧加角色约束），
        // 或者改成由种子数据登记。现在留在这一档，是因为替代方案是"系统起不来"。
        .RequireAuthenticated();

        // ---------- 授权判定 ----------

        identity.MapPost("/authorize", async (ApiResponses responses,
            AuthorizeRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.QueryAsync(
                new AuthorizeQuery(request.UserId, request.Path, request.Method),
                cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Ok(new AuthorizationResponse(request.UserId, result.Value.RequiredKey,
                    result.Value.Decision, result.Value.GrantedCount));
        }).Produces<ApiResponse<AuthorizationResponse>>().ProducesApiErrors(404).RequirePermission("/api/identity/authorize", "POST");

        // ---------- 菜单 ----------
        //
        // 菜单是**权限链的第一环**（票据 67）：菜单 → api-resource 挂在它下面 →
        // 角色被授予菜单 → 用户拿到角色 → 权限键集合非空。
        //
        // 在这一组端点存在之前，`MenuTree.AddRoot` 在领域层写好、也有测试，
        // 却**没有任何调用者**——于是整条链永远断在第一环，所有受权限保护的端点永远 403，
        // 而所有测试都是绿的（它们直接构造聚合，绕过了"能不能建出那个聚合"）。
        //
        // **用 `RequirePermission` 而不是 `RequireAuthenticated`**：根账号靠 `IsRoot` 旁路通过
        // （`AccessPolicy` 里那条旁路此前不可达，票据 71 决定了它的来源），
        // 而普通用户没有这个键 → 403。它也顺带把"菜单管理可以授权给某个角色"留成了正规路径：
        // 登记一条 `/api/identity/menus` + `POST` 的 api-resource 并授予即可，不必改代码。

        identity.MapGet("/menus", async (ApiResponses responses, ISender sender, CancellationToken cancellationToken) =>
        {
            var result = await sender.QueryAsync(new GetMenusQuery(), cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Ok(new MenuCollectionResponse(result.Value.Count, result.Value));
        }).Produces<ApiResponse<MenuCollectionResponse>>().RequirePermission("/api/identity/menus", "GET");

        identity.MapPost("/menus", async (ApiResponses responses,
            CreateMenuRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(
                new CreateMenuCommand(request.Title, request.SortOrder, request.ParentMenuId),
                cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Created($"/api/identity/menus/{result.Value.MenuId}", result.Value);
        }).Produces<ApiResponse<MenuCreated>>(201).RequirePermission("/api/identity/menus", "POST");

        return endpoints;
    }

    /// <summary>
    /// 本模块自己的错误码 → 状态码映射。
    /// <para>与 Platform（全 400）、Files（not_found 404）、Scheduling（not_found 404）**故意不同**：
    /// 这里还区分 409 冲突——用户名/角色编码被占用不是"请求错了"，是"状态冲突"。</para>
    /// </summary>
    private static IResult Failure(Error error) => Results.Problem(
        statusCode: error.Code switch
        {
            "identity.user.not_found" or "identity.role.not_found" => StatusCodes.Status404NotFound,
            "identity.user_name.taken" or "identity.role_code.taken" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        },
        title: error.Message,
        extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

/// <summary>登录。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="Password">明文口令——**只在这一次调用里存在**。</param>
/// <param name="Captcha">验证码答案；没有启用验证码时为 <c>null</c>。</param>
internal sealed record LoginRequest(string UserName, string Password, string? Captcha);

/// <summary>刷新令牌。</summary>
/// <param name="RefreshToken">刷新令牌原文。</param>
internal sealed record RefreshRequest(string RefreshToken);

/// <summary>创建用户。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="Password">明文口令——<b>只在这一次调用里存在</b>，进领域前已被换成哈希。</param>
internal sealed record CreateUserRequest(string UserName, string Password);

/// <summary>创建角色。</summary>
/// <param name="Code">角色编码。</param>
/// <param name="Name">角色名称。</param>
internal sealed record CreateRoleRequest(string Code, string Name);

/// <summary>注册一个 API 资源。</summary>
/// <param name="MenuId">所属菜单；<c>null</c> 表示不对应菜单（不会被菜单授权覆盖）。</param>
/// <param name="Path">路由模板。</param>
/// <param name="Method">HTTP 方法。</param>
internal sealed record CreateApiResourceRequest(string Path, string Method, long? MenuId);

/// <summary>授权判定请求。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="Path">请求路径。</param>
/// <param name="Method">HTTP 方法。</param>
internal sealed record AuthorizeRequest(long UserId, string Path, string Method);

/// <summary>创建一个菜单节点。</summary>
/// <param name="Title">标题。</param>
/// <param name="SortOrder">同级排序；不传按 0。</param>
/// <param name="ParentMenuId">父菜单；<c>null</c>（不传）表示根节点。</param>
internal sealed record CreateMenuRequest(string Title, int SortOrder, long? ParentMenuId);

internal sealed record LoginResponse(long UserId, string UserName, string AccessToken, DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
internal sealed record RefreshResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
internal sealed record UserCreatedResponse(long UserId);
internal sealed record RoleCreatedResponse(long RoleId);
internal sealed record UserPermissionsResponse(long UserId, IReadOnlyList<string> Keys);
internal sealed record AuthorizationResponse(long UserId, string RequiredKey, string Decision, int GrantedCount);
internal sealed record MenuCollectionResponse(int Count, IReadOnlyList<MenuView> Items);
