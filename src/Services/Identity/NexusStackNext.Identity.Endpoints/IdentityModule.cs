using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.Identity.Infrastructure.Persistence;

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
        var sessionRead = configuration.GetSection("Identity:SessionAuthority").Get<IdentitySessionReadOptions>() ?? new();
        sessionRead.Validate();
        services.AddSingleton(sessionRead);
        var capacityRead = configuration.GetSection("Identity:AuditDelivery:CapacityRead").Get<CommittedFactCapacityReadOptions>() ?? new();
        capacityRead.Validate();
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

            services.AddIdentityInMemoryStorage(configuration.GetSection("Identity:AuditDelivery:MemoryCapacity").Get<MemoryCommittedFactCapacityOptions>(),
                configuration.GetSection("Identity:AuditDelivery:CapacityWrite").Get<CommittedFactCapacityWriteOptions>(),
                configuration.GetSection("Identity:AuditDelivery:MemoryPolicyControl").Get<MemoryFactCapacityPolicyControlOptions>(),
                configuration.GetSection("Identity:AuditDelivery:MemoryRecoveryControl").Get<MemoryFactDeliveryRecoveryControlOptions>());
            services.AddIdentityMemoryFactCleanup(configuration.GetSection("Identity:AuditDelivery:Cleanup").Get<CommittedFactCleanupOptions>());
        }
        else if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connection = configuration.GetConnectionString("Identity");
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new InvalidOperationException("必须配置 ConnectionStrings:Identity；开发测试可显式选择 Identity:Storage:Provider=Memory。");
            }

            var capacityWrite = configuration.GetSection("Identity:AuditDelivery:CapacityWrite").Get<CommittedFactCapacityWriteOptions>() ?? new();
            services.AddIdentityEntityFrameworkStorage(capacityWrite.ConfigureConnection(connection));
            services.AddCommittedFactCapacityReader<IdentityDbContext>("identity", capacityRead);
            services.AddKeyedScoped<PostgresFactCapacityPolicyStore>("identity", (serviceProvider, _) => new(
                connection, serviceProvider.GetRequiredService<IIntegrationEventSerializer>(), capacityRead.Timeout,
                new("identity", IdentityFactCapacityPolicyChangedV1.From)));
            services.AddKeyedScoped<ICommittedFactCapacityPolicyStore>("identity", (serviceProvider, _) => serviceProvider.GetRequiredKeyedService<PostgresFactCapacityPolicyStore>("identity"));
            services.AddKeyedScoped<ICommittedFactCapacityPolicyCleanup>("identity", (serviceProvider, _) => serviceProvider.GetRequiredKeyedService<PostgresFactCapacityPolicyStore>("identity"));
            services.AddIdentityDatabaseChecks();
            services.AddCommittedFactCleanup<IdentityDbContext>("identity", IdentityEntityCommittedV1.Name,
                configuration.GetSection("Identity:AuditDelivery:Cleanup").Get<CommittedFactCleanupOptions>());
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

        // **根账号播种**：每次启动跑一次，已有内建根账号则跳过；普通账号占名时拒绝启动。
        // 它在这里注册而不是在各宿主里：这是 Identity 自己的引导，五个宿主不该各写一遍。
        services.AddHostedService<RootAccountSeeder>();

        return services.AddCommittedFactPolicyMaintenance("identity", configuration.GetSection("Identity:AuditDelivery:PolicyMaintenance")
            .Get<FactCapacityPolicyMaintenanceOptions>())
            .AddFactDeliveryRecoveryMaintenance<IIdentityAuditDelivery>("identity", configuration.GetSection("Identity:AuditDelivery:RecoveryMaintenance")
                .Get<FactDeliveryRecoveryMaintenanceOptions>());
    }

    /// <summary>映射本模块的端点。</summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // 只做框架验签；当前会话判定自身不能再调用会话过滤器。
        endpoints.MapGet("/api/identity/session/v1", async (ICurrentUser caller, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            if (!long.TryParse(caller.UserId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var userId))
            { return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "需要先认证。"); }
            var result = await sender.QueryAsync(new GetCurrentSessionQuery(userId, caller.SessionVersion), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value)
                : Results.Problem(statusCode: result.Error == SessionValidationErrors.Invalid ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable,
                    title: result.Error.Message, extensions: new Dictionary<string, object?> { ["errorCode"] = result.Error.Code });
        }).RequireAuthorization().Produces<ApiResponse<CurrentSessionV1>>().ProducesApiErrors(401, 503)
            .WithMetadata(new OperationLogSuppression("内部权威读取不产生额外操作观察，来源业务请求仍记录授权结果，避免每次授权放大日志。"));

        endpoints.MapGet("/api/identity/access/v1", async (string permissionKey, ICurrentUser caller, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            if (!TrySubject(caller, out var userId)) { return Results.Unauthorized(); }
            if (permissionKey.Length > 280 || !PermissionKey.TryParse(permissionKey, out var operation))
            { return Failure(new("identity.access.key_invalid", "必须提供有界操作权限键。")); }
            var result = await sender.QueryAsync(new GetCurrentAccessQuery(userId, caller.SessionVersion, operation), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value)
                : Results.Problem(statusCode: result.Error == SessionValidationErrors.Invalid ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable,
                    title: result.Error.Message, extensions: new Dictionary<string, object?> { ["errorCode"] = result.Error.Code });
        }).RequireAuthorization().Produces<ApiResponse<CurrentAccessV1>>().ProducesApiErrors(400, 401, 503)
            .WithMetadata(new OperationLogSuppression("内部当前访问判定不产生额外操作观察；用户业务请求独立保留授权结果。"));

        var identity = endpoints.MapGroup("/api/identity").ProducesApiErrors(400, 401, 403, 500, 503);

        // **授权过滤器挂在整个分组上**，于是"这个模块的端点默认都要过一遍授权"是结构性的，
        // 而不是每个端点各自的记性。公开的端点由框架的 `AllowAnonymous()` 显式标注。
        identity.AddEndpointFilter<NexusStackAuthorizationFilter>();

        identity.MapGet("/audit-capacity", async ([FromKeyedServices("identity")] ICommittedFactCapacityPolicyStore policies,
            ApiResponses responses, CancellationToken token) =>
        {
            var diagnostic = await policies.ReadPolicyAsync(token).ConfigureAwait(false);
            return diagnostic.IsSuccess ? (IResult)responses.Ok(diagnostic.Value) : Failure(diagnostic.Error);
        }).RequireAuthorization().RequirePermission("/api/identity/audit-capacity", "GET")
            .Produces<ApiResponse<FactCapacityPolicySnapshot>>();

        identity.MapPut("/audit-capacity", async (FactCapacityPolicyRequest request, ICurrentUser user,
            IClock clock, IExecutionContext execution, ApiResponses responses, CancellationToken token,
            [FromKeyedServices("identity")] ICommittedFactCapacityPolicyStore policies) =>
        {
            var result = await policies.AdjustAsync(request, user.UserId ?? string.Empty, clock.UtcNow,
                execution.Capture(), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequireAuthorization().RequirePermission("/api/identity/audit-capacity", "PUT")
            .Produces<ApiResponse<FactCapacityPolicyReceipt>>().ProducesApiErrors(409, 415)
            .WithMetadata(new OperationDescription("identity.fact-capacity-policy.adjust", "调整所属事实容量策略"));

        identity.MapGet("/audit-deliveries", async ([FromServices] IIdentityAuditDelivery delivery,
            ApiResponses responses, CancellationToken token, string state = "Pending", int limit = 50) =>
        {
            if (state is not ("Pending" or "Delivered" or "DeadLettered") || limit is < 1 or > 100)
            { return Failure(new Error("identity.delivery_query.invalid", "投递状态必须为 Pending、Delivered 或 DeadLettered，limit 必须在 1 到 100。")); }
            return (IResult)responses.Ok(await delivery.ListAsync(state, limit, token).ConfigureAwait(false));
        }).RequireAuthorization().RequirePermission("/api/identity/audit-deliveries", "GET")
            .Produces<ApiResponse<IReadOnlyList<FactDeliveryState>>>();

        identity.MapGet("/audit-deliveries/{messageId:guid}", async (Guid messageId,
            [FromServices] IIdentityAuditDelivery delivery, ApiResponses responses, CancellationToken token) =>
        {
            var result = await delivery.GetAsync(messageId, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequireAuthorization().RequirePermission("/api/identity/audit-deliveries/{messageId}", "GET")
            .Produces<ApiResponse<FactDeliveryState>>().ProducesApiErrors(404);

        identity.MapGet("/audit-deliveries/recovery-capacity", async (
            [FromServices] IIdentityAuditDelivery delivery, ApiResponses responses, CancellationToken token) =>
        {
            var capacity = await delivery.ReadRecoveryCapacityAsync(token).ConfigureAwait(false);
            return capacity.IsSuccess ? (IResult)responses.Ok(capacity.Value) : Failure(capacity.Error);
        }).RequireAuthorization().RequirePermission("/api/identity/audit-deliveries/recovery-capacity", "GET")
            .Produces<ApiResponse<FactDeliveryRecoveryCapacity>>();

        identity.MapPost("/audit-deliveries/{messageId:guid}/retry", async (Guid messageId, RetryIdentityAuditDeliveryRequest request,
            [FromServices] IIdentityAuditDelivery delivery, ICurrentUser user, IClock clock, IExecutionContext execution,
            ApiResponses responses, CancellationToken token) =>
        {
            var recovered = await delivery.RecoverAsync(new(request.RequestId, messageId, request.ExpectedDeadLetteredAt,
                request.ExpectedRetryRevision, request.Reason), user.UserId ?? string.Empty, clock.UtcNow,
                execution.Capture(), token).ConfigureAwait(false);
            return recovered.IsSuccess ? (IResult)responses.Ok(recovered.Value) : Failure(recovered.Error);
        }).RequireAuthorization().RequirePermission("/api/identity/audit-deliveries/{messageId}/retry", "POST")
            .Produces<ApiResponse<FactDeliveryRecoveryReceipt>>().ProducesApiErrors(409, 415)
            .WithMetadata(new OperationDescription("identity.fact-delivery.recover", "恢复所属事实投递"));

        identity.MapGet("/audit-deliveries/recoveries/{requestId:guid}", async (Guid requestId,
            [FromServices] IIdentityAuditDelivery delivery, ApiResponses responses, CancellationToken token) =>
        {
            var receipt = await delivery.GetRecoveryAsync(requestId, token).ConfigureAwait(false);
            return receipt.IsSuccess ? (IResult)responses.Ok(receipt.Value) : Failure(receipt.Error);
        }).RequireAuthorization().RequirePermission("/api/identity/audit-deliveries/recoveries/{requestId}", "GET")
            .Produces<ApiResponse<FactDeliveryRecoveryReceipt>>().ProducesApiErrors(404);

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
        }).ProducesApiErrors(415).Produces<ApiResponse<LoginResponse>>().AllowAnonymous();   // 登录当然要公开——它是拿钥匙的地方。

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
        }).ProducesApiErrors(415).Produces<ApiResponse<RefreshResponse>>().AllowAnonymous();   // 刷新也一样：访问令牌过期时，客户端手里只有刷新令牌。

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

        identity.MapPost("/me/password", async (OwnPasswordRequest request, ICurrentUser current, ISender sender, CancellationToken token) =>
        {
            if (!TrySubject(current, out var userId) || current.SessionVersion is not { } version) { return Results.Unauthorized(); }
            var result = await sender.SendAsync(new ChangeOwnPasswordCommand(userId, version, request.ExpectedVersion, request.OldPassword, request.NewPassword), token);
            return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
        }).RequireAuthenticated().Produces(204).ProducesApiErrors(409, 415)
            .WithMetadata(new OperationDescription("identity.user.password-change", "本人轮换口令"));

        identity.MapPost("/users/{userId:long}/password", async (long userId, ResetPasswordRequest request, ISender sender, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new ResetUserPasswordCommand(userId, request.ExpectedVersion, request.NewPassword), token);
            return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
        }).RequirePermission("/api/identity/users/{userId}/password", "POST").Produces(204).ProducesApiErrors(404, 409, 415)
            .WithMetadata(new OperationDescription("identity.user.password-reset", "管理者重置口令"));

        identity.MapPost("/users/{userId:long}/disable", async (long userId, UserVersionRequest request, ISender sender, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new SetUserEnabledCommand(userId, request.ExpectedVersion, false), token);
            return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
        }).RequirePermission("/api/identity/users/{userId}/disable", "POST").Produces(204).ProducesApiErrors(404, 409, 415)
            .WithMetadata(new OperationDescription("identity.user.disable", "禁用用户并撤销会话"));

        identity.MapPost("/users/{userId:long}/enable", async (long userId, UserVersionRequest request, ISender sender, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new SetUserEnabledCommand(userId, request.ExpectedVersion, true), token);
            return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
        }).RequirePermission("/api/identity/users/{userId}/enable", "POST").Produces(204).ProducesApiErrors(404, 409, 415)
            .WithMetadata(new OperationDescription("identity.user.enable", "启用用户"));

        identity.MapGet("/users", async (long? afterUserId, int? limit, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetUsersQuery(afterUserId ?? 0, limit ?? 50), token);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/identity/users", "GET").Produces<ApiResponse<UserPage>>();

        identity.MapGet("/users/{userId:long}", async (long userId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetUserQuery(userId), token);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/identity/users/{userId}", "GET").Produces<ApiResponse<UserView>>().ProducesApiErrors(404);

        identity.MapGet("/me", async (ICurrentUser current, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            if (!TrySubject(current, out var userId)) { return Results.Unauthorized(); }
            var result = await sender.QueryAsync(new GetUserQuery(userId), token);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequireAuthenticated().Produces<ApiResponse<UserView>>();

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
        }).ProducesApiErrors(415).Produces<ApiResponse<UserCreatedResponse>>(201).ProducesApiErrors(409)
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

        identity.MapPost("/users/{userId:long}/roles/{roleId:long}/revoke", async (long userId, long roleId,
            UserVersionRequest request, ISender sender, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RevokeRoleCommand(userId, roleId, request.ExpectedVersion), token);
            return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
        }).RequirePermission("/api/identity/users/{userId}/roles/{roleId}/revoke", "POST").Produces(204).ProducesApiErrors(404, 409, 415)
            .WithMetadata(new OperationDescription("identity.user.role-revoke", "撤销用户角色"));

        // ---------- 角色 ----------

        identity.MapGet("/roles/{roleId:long}", async (long roleId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetRoleQuery(roleId), token);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/identity/roles/{roleId}", "GET").Produces<ApiResponse<RoleView>>().ProducesApiErrors(404);

        identity.MapPut("/roles/{roleId:long}/menus", async (long roleId, ReplaceRoleMenusRequest request, ISender sender, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new ReplaceRoleMenusCommand(roleId, request.ExpectedVersion, request.MenuIds), token);
            return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
        }).RequirePermission("/api/identity/roles/{roleId}/menus", "PUT").Produces(204).ProducesApiErrors(404, 409, 415)
            .WithMetadata(new OperationDescription("identity.role.menus-replace", "替换角色菜单授权"));

        identity.MapPost("/roles", async (ApiResponses responses,
            CreateRoleRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.SendAsync(new CreateRoleCommand(request.Code, request.Name), cancellationToken);

            return result.IsFailure
                ? Failure(result.Error)
                : responses.Created($"/api/identity/roles/{result.Value}", new RoleCreatedResponse(result.Value));
        }).ProducesApiErrors(415).Produces<ApiResponse<RoleCreatedResponse>>(201).ProducesApiErrors(409).RequirePermission("/api/identity/roles", "POST");

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
        }).ProducesApiErrors(415).Produces<ApiResponse<ApiResourceCreated>>(201)
        // 根账号通过既有旁路引导首个资源；之后可显式委派资源管理权。
        // 仅有某个菜单的使用权，不能向该菜单添加新的管理权限。
        .RequirePermission("/api/identity/api-resources", "POST");

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
        }).ProducesApiErrors(415).Produces<ApiResponse<AuthorizationResponse>>().ProducesApiErrors(404).RequirePermission("/api/identity/authorize", "POST");

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
        }).ProducesApiErrors(415).Produces<ApiResponse<MenuCreated>>(201).RequirePermission("/api/identity/menus", "POST");

        return endpoints;
    }

    private static bool TrySubject(ICurrentUser current, out long userId)
        => long.TryParse(current.UserId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out userId) && userId > 0;

    /// <summary>
    /// 本模块自己的错误码 → 状态码映射。
    /// <para>与 Platform（全 400）、Files（not_found 404）、Scheduling（not_found 404）**故意不同**：
    /// 这里还区分 409 冲突——用户名/角色编码被占用不是"请求错了"，是"状态冲突"。</para>
    /// </summary>
    private static IResult Failure(Error error) => Results.Problem(
        statusCode: error.Code switch
        {
            "audit_capacity.unavailable" or "audit_capacity.busy" => StatusCodes.Status503ServiceUnavailable,
            "identity.user.not_found" or "identity.role.not_found" or "identity.menu.not_found" or "identity.delivery_recovery.not_found" or "identity.delivery_not_found" => StatusCodes.Status404NotFound,
            "identity.delivery_conflict" or "identity.delivery_recovery.request_conflict" => StatusCodes.Status409Conflict,
            "identity.delivery_recovery.exhausted" => StatusCodes.Status503ServiceUnavailable,
            "identity.user_name.taken" or "identity.role_code.taken" => StatusCodes.Status409Conflict,
            "identity.audit_policy.conflict" => StatusCodes.Status409Conflict,
            "identity.user.conflict" => StatusCodes.Status409Conflict,
            "identity.role.conflict" => StatusCodes.Status409Conflict,
            "identity.session.invalid" => StatusCodes.Status401Unauthorized,
            "identity.audit_policy.control_exhausted" => StatusCodes.Status503ServiceUnavailable,
            "identity.audit_capacity.exhausted" => StatusCodes.Status503ServiceUnavailable,
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

internal sealed record UserVersionRequest([property: JsonRequired] long ExpectedVersion);
internal sealed record ReplaceRoleMenusRequest([property: JsonRequired] long ExpectedVersion, [property: JsonRequired] IReadOnlyList<long>? MenuIds);
internal sealed record OwnPasswordRequest([property: JsonRequired] long ExpectedVersion, string OldPassword, string NewPassword);
internal sealed record ResetPasswordRequest([property: JsonRequired] long ExpectedVersion, string NewPassword);

internal sealed record RetryIdentityAuditDeliveryRequest(Guid RequestId, DateTimeOffset ExpectedDeadLetteredAt,
    [property: JsonRequired] long ExpectedRetryRevision, string Reason);

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
