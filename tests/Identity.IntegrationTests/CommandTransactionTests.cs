using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Application.Transactions;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Identity.IntegrationTests;

[Collection(IdentityDatabaseGroup.Name)]
public sealed class CommandTransactionTests(IdentityDatabaseFixture fixture)
{
    [PostgresFact]
    public async Task UserAudit_TracksCommittedChanges_PreservesCreationAndIgnoresNoOpsAndRollbacks()
    {
        await fixture.ResetAsync();
        var createdAt = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(createdAt);
        var actor = new MutableCurrentUser("creator");
        var gate = new CommitGate { RejectCommit = true };
        gate.Release.TrySetResult();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString, services =>
        {
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<ICurrentUser>(actor);
            services.ConfigureDbContext<IdentityDbContext>(options => options.AddInterceptors(gate));
        });
        var user = await SendAsync(provider, new CreateUserCommand("audit-user", "a-strong-password"));
        var firstRole = await SendAsync(provider, new CreateRoleCommand("audit-first", "First"));
        var secondRole = await SendAsync(provider, new CreateRoleCommand("audit-second", "Second"));
        Assert.True(user.IsSuccess);
        Assert.True(firstRole.IsSuccess);
        Assert.True(secondRole.IsSuccess);
        var created = await ReadAuditAsync(user.Value);
        Assert.Equal(new EntityAuditSnapshot(createdAt, "creator", null, null, 1), created);

        actor.UserId = "editor";
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await SendAsync(provider, new AssignRoleCommand(user.Value, firstRole.Value))).IsSuccess);
        var modified = await ReadAuditAsync(user.Value);
        Assert.Equal(new EntityAuditSnapshot(createdAt, "creator", clock.UtcNow, "editor", 2), modified);

        actor.UserId = "no-op-caller";
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await SendAsync(provider, new AssignRoleCommand(user.Value, firstRole.Value))).IsSuccess);
        Assert.Equal(modified, await ReadAuditAsync(user.Value));

        actor.UserId = "rolled-back-caller";
        clock.Advance(TimeSpan.FromHours(1));
        gate.Arm();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(provider, new AssignRoleCommand(user.Value, secondRole.Value)));
        Assert.Equal(modified, await ReadAuditAsync(user.Value));
        await using var verify = fixture.NewContext();
        var reloaded = await new EfUserRepository(verify).FindAsync(new UserId(user.Value));
        Assert.NotNull(reloaded);
        Assert.Equal(firstRole.Value, Assert.Single(reloaded.RoleIds).Value);
    }

    [PostgresFact]
    public async Task PermissionRootAudit_PersistsRoleResourceAndTree_AndTracksOwnedChangesOnly()
    {
        await fixture.ResetAsync();
        var createdAt = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(createdAt);
        var actor = new MutableCurrentUser("permission-creator") { IsRoot = true };
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString, services =>
        {
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<ICurrentUser>(actor);
        });
        var role = await SendAsync(provider, new CreateRoleCommand("audited-role", "Audited role"));
        var menu = await SendAsync(provider, new CreateMenuCommand("Audited root", 1, null));
        Assert.True(role.IsSuccess);
        Assert.True(menu.IsSuccess);
        var resource = await SendAsync(provider, new CreateApiResourceCommand("/audit/permission-root", "GET", menu.Value.MenuId));
        Assert.True(resource.IsSuccess);

        var initial = await ReadPermissionAuditsAsync(provider, role.Value, menu.Value.MenuId, resource.Value.ApiResourceId);
        var newlyCreated = new EntityAuditSnapshot(createdAt, "permission-creator", null, null, 1);
        Assert.Equal(newlyCreated, initial.Role);
        Assert.Equal(newlyCreated, initial.Resource);
        // 新建树和第一个根节点在同一次创建中保存，仍然没有修改审计。
        Assert.Equal(newlyCreated with { Version = 2 }, initial.Tree);

        actor.UserId = "permission-editor";
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await SendAsync(provider, new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).IsSuccess);
        var child = await SendAsync(provider, new CreateMenuCommand("Audited child", 1, menu.Value.MenuId));
        Assert.True(child.IsSuccess);
        var modified = await ReadPermissionAuditsAsync(provider, role.Value, menu.Value.MenuId, resource.Value.ApiResourceId);
        var edited = newlyCreated with { UpdatedAt = clock.UtcNow, UpdatedBy = actor.UserId, Version = 2 };
        Assert.Equal(edited, modified.Role);
        Assert.Equal(edited with { Version = 3 }, modified.Tree);
        Assert.Equal(initial.Resource, modified.Resource);
        await using (var scope = provider.CreateAsyncScope())
        {
            var tree = await scope.ServiceProvider.GetRequiredService<IMenuTreeRepository>().FindAsync();
            Assert.NotNull(tree);
            Assert.Equal(menu.Value.MenuId, tree.Find(new MenuId(child.Value.MenuId))?.ParentId?.Value);
        }

        actor.UserId = "permission-no-op";
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await SendAsync(provider, new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).IsSuccess);
        Assert.Equal(modified, await ReadPermissionAuditsAsync(provider, role.Value, menu.Value.MenuId, resource.Value.ApiResourceId));
    }

    private static async Task<(EntityAuditSnapshot Role, EntityAuditSnapshot Resource, EntityAuditSnapshot Tree)> ReadPermissionAuditsAsync(
        ServiceProvider provider, long roleId, long menuId, long resourceId)
    {
        await using var scope = provider.CreateAsyncScope();
        var roles = await scope.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(roleId)]);
        var role = Assert.Single(roles);
        var resources = await scope.ServiceProvider.GetRequiredService<IApiResourceRepository>()
            .FindByMenuIdsAsync(new HashSet<MenuId> { new(menuId) });
        var resource = Assert.Single(resources);
        Assert.Equal(resourceId, resource.Id.Value);
        var tree = await scope.ServiceProvider.GetRequiredService<IMenuTreeRepository>().FindAsync();
        Assert.NotNull(tree);
        return (AuditOf(role), AuditOf(resource), AuditOf(tree));
    }

    private static EntityAuditSnapshot AuditOf<TId>(AuditedAggregateRoot<TId> root) where TId : notnull =>
        new(root.CreatedAt, root.CreatedBy, root.UpdatedAt, root.UpdatedBy, root.Version);

    private async Task<EntityAuditSnapshot> ReadAuditAsync(long id)
    {
        await using var context = fixture.NewContext();
        var user = await new EfUserRepository(context).FindAsync(new UserId(id));
        Assert.NotNull(user);
        var audit = Assert.IsAssignableFrom<IAuditedEntity>(user);
        return new(audit.CreatedAt, audit.CreatedBy, audit.UpdatedAt, audit.UpdatedBy, user.Version);
    }

    private sealed record EntityAuditSnapshot(DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? UpdatedAt, string? UpdatedBy, long Version);

    [PostgresFact]
    public async Task ReplayRejection_PersistsRevocation_ForTheNextRequest()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        Assert.True((await SendAsync(provider, new CreateUserCommand("replay-commit", "a-strong-password"))).IsSuccess);
        var login = await SendAsync(provider, new LoginCommand("replay-commit", "a-strong-password"));
        Assert.True(login.IsSuccess);
        var refreshed = await SendAsync(provider, new RefreshTokenCommand(login.Value.Tokens.RefreshToken));
        Assert.True(refreshed.IsSuccess);

        var replay = await SendAsync(provider, new RefreshTokenCommand(login.Value.Tokens.RefreshToken));
        Assert.True(replay.IsFailure);

        // 每次 SendAsync 都打开新作用域，不能从上一条命令的跟踪器读到“已撤销”的假象。
        var nextRequest = await SendAsync(provider, new RefreshTokenCommand(refreshed.Value.RefreshToken));
        Assert.True(nextRequest.IsFailure);
        Assert.Equal("identity.refresh_token.unusable", nextRequest.Error.Code);
    }

    [PostgresFact]
    public async Task FailedLogoutCommit_DoesNotRevokeTheSession_AndSuccessfulLogoutDoes()
    {
        await fixture.ResetAsync();
        var gate = new CommitGate { RejectCommit = true };
        gate.Release.TrySetResult();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString, services =>
            services.ConfigureDbContext<IdentityDbContext>(options => options.AddInterceptors(gate)));
        var user = await SendAsync(provider, new CreateUserCommand("session-commit", "a-strong-password"));
        Assert.True(user.IsSuccess);
        var login = await SendAsync(provider, new LoginCommand("session-commit", "a-strong-password"));
        Assert.True(login.IsSuccess);

        gate.Arm();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(provider, new LogoutCommand(user.Value)));
        var afterFailure = await SendAsync(provider, new RefreshTokenCommand(login.Value.Tokens.RefreshToken));
        Assert.True(afterFailure.IsSuccess);
        Assert.Equal(SessionOf(login.Value.Tokens), SessionOf(afterFailure.Value));

        Assert.True((await SendAsync(provider, new LogoutCommand(user.Value))).IsSuccess);
        Assert.True((await SendAsync(provider, new RefreshTokenCommand(afterFailure.Value.RefreshToken))).IsFailure);
        var afterLogout = await SendAsync(provider, new LoginCommand("session-commit", "a-strong-password"));
        Assert.True(afterLogout.IsSuccess);
        Assert.NotEqual(SessionOf(login.Value.Tokens), SessionOf(afterLogout.Value.Tokens));
    }

    [PostgresFact]
    public async Task FailedCommit_CanBeRetriedInTheSameScope_WithoutLeakingPermissions()
    {
        await fixture.ResetAsync();
        var gate = new CommitGate { RejectCommit = true };
        gate.Release.TrySetResult();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString, services =>
            services.ConfigureDbContext<IdentityDbContext>(options => options.AddInterceptors(gate)));
        var user = await SendAsync(provider, new CreateUserCommand("commit-retry", "a-strong-password"));
        var role = await SendAsync(provider, new CreateRoleCommand("reader", "Reader"));
        Assert.True(user.IsSuccess);
        Assert.True(role.IsSuccess);
        var menu = await SendAsync(provider, new CreateMenuCommand("Transaction permissions", 1, null));
        Assert.True(menu.IsSuccess);
        Assert.True((await SendAsync(provider, new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
        Assert.True((await SendAsync(provider, new CreateApiResourceCommand("/transactions", "GET", menu.Value.MenuId))).IsSuccess);
        Assert.Empty((await PermissionsAsync(provider, user.Value)).Value);

        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        gate.Arm();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId)));
        Assert.Empty((await PermissionsAsync(provider, user.Value)).Value);

        Assert.True((await sender.SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).IsSuccess);
        Assert.Equal("/transactions:GET", Assert.Single((await PermissionsAsync(provider, user.Value)).Value));
    }

    [PostgresFact]
    public async Task PermissionReadAfterCommit_DoesNotJoinAnOlderReadStillInFlight()
    {
        await fixture.ResetAsync();
        var gate = new PermissionReadGate();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString, services =>
            services.ConfigureDbContext<IdentityDbContext>(options => options.AddInterceptors(gate)));
        var user = await SendAsync(provider, new CreateUserCommand("permission-flight", "a-strong-password"));
        var role = await SendAsync(provider, new CreateRoleCommand("reader", "Reader"));
        Assert.True(user.IsSuccess);
        Assert.True(role.IsSuccess);
        var menu = await SendAsync(provider, new CreateMenuCommand("Transaction permissions", 1, null));
        Assert.True(menu.IsSuccess);
        Assert.True((await SendAsync(provider, new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).IsSuccess);
        Assert.True((await SendAsync(provider, new CreateApiResourceCommand("/transactions", "GET", menu.Value.MenuId))).IsSuccess);

        gate.Arm();
        var oldRead = PermissionsAsync(provider, user.Value);
        Task<BuildingBlocks.Domain.Result<IReadOnlyList<string>>> newRead;
        try
        {
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True((await SendAsync(provider, new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
            newRead = PermissionsAsync(provider, user.Value);
        }
        finally
        {
            gate.Release.TrySetResult();
            await oldRead;
        }

        Assert.Equal("/transactions:GET", Assert.Single((await newRead).Value));
        Assert.Equal("/transactions:GET", Assert.Single((await PermissionsAsync(provider, user.Value)).Value));
    }

    [PostgresFact]
    public async Task PermissionReadDuringCommit_DoesNotCacheOldPermissionsAfterCommit()
    {
        await fixture.ResetAsync();
        var gate = new CommitGate();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString, services =>
            services.ConfigureDbContext<IdentityDbContext>(options => options.AddInterceptors(gate)));
        var user = await SendAsync(provider, new CreateUserCommand("permission-commit", "a-strong-password"));
        var role = await SendAsync(provider, new CreateRoleCommand("reader", "Reader"));
        Assert.True(user.IsSuccess);
        Assert.True(role.IsSuccess);
        var menu = await SendAsync(provider, new CreateMenuCommand("Transaction permissions", 1, null));
        Assert.True(menu.IsSuccess);
        Assert.True((await SendAsync(provider, new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
        Assert.True((await SendAsync(provider, new CreateApiResourceCommand("/transactions", "GET", menu.Value.MenuId))).IsSuccess);
        Assert.Empty((await PermissionsAsync(provider, user.Value)).Value);

        gate.Arm();
        var grant = SendAsync(provider, new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId));
        try
        {
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Empty((await PermissionsAsync(provider, user.Value)).Value);
        }
        finally
        {
            gate.Release.TrySetResult();
            Assert.True((await grant).IsSuccess);
        }

        var after = await PermissionsAsync(provider, user.Value);
        Assert.Equal("/transactions:GET", Assert.Single(after.Value));
    }

    [PostgresFact]
    public async Task FailedRefreshIssuance_LeavesTheOriginalTokenUsable()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        Assert.True((await SendAsync(provider, new CreateUserCommand("refresh-rollback", "a-strong-password"))).IsSuccess);
        var login = await SendAsync(provider, new LoginCommand("refresh-rollback", "a-strong-password"));
        Assert.True(login.IsSuccess);

        await using var misconfigured = IdentityTestHost.Build(fixture.Database.ConnectionString,
            services => services.AddSingleton(Options.Create(new JwtOptions { SigningKey = "too-short" })));
        var rejected = await SendAsync(misconfigured, new RefreshTokenCommand(login.Value.Tokens.RefreshToken));
        Assert.Equal("identity.token.signing_key_too_short", rejected.Error.Code);

        var retried = await SendAsync(provider, new RefreshTokenCommand(login.Value.Tokens.RefreshToken));
        Assert.True(retried.IsSuccess, retried.Error.Message);
    }

    [PostgresFact]
    public async Task AnotherContextsUnitOfWork_DoesNotStealIdentityChanges()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString, services =>
        {
            services.AddDbContext<OtherContext>(options => options.UseNpgsql(fixture.Database.ConnectionString));
            services.AddScoped<IUnitOfWork, EfUnitOfWork<OtherContext>>();
        });

        var user = await SendAsync(provider, new CreateUserCommand("transaction-owner", "a-strong-password"));
        var role = await SendAsync(provider, new CreateRoleCommand("reader", "Reader"));
        Assert.True(user.IsSuccess);
        Assert.True(role.IsSuccess);
        var menu = await SendAsync(provider, new CreateMenuCommand("Transaction permissions", 1, null));
        Assert.True(menu.IsSuccess);
        Assert.True((await SendAsync(provider, new CreateApiResourceCommand("/transactions", "GET", menu.Value.MenuId))).IsSuccess);
        Assert.True((await IdentityTestHost.InScopeAsync(provider, scope => scope.GetRequiredService<ISender>()
            .SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId)))).IsSuccess);
        Assert.True((await IdentityTestHost.InScopeAsync(provider, scope => scope.GetRequiredService<ISender>()
            .SendAsync(new AssignRoleCommand(user.Value, role.Value)))).IsSuccess);

        var permissions = await IdentityTestHost.InScopeAsync(provider, scope => scope.GetRequiredService<ISender>()
            .QueryAsync(new GetUserPermissionsQuery(user.Value)));

        Assert.True(permissions.IsSuccess);
        Assert.Equal("/transactions:GET", Assert.Single(permissions.Value));
    }

    private static Task<BuildingBlocks.Domain.Result<T>> SendAsync<T>(ServiceProvider provider, ICommand<T> command) =>
        IdentityTestHost.InScopeAsync(provider, scope => scope.GetRequiredService<ISender>().SendAsync(command));

    private static string SessionOf(TokenPair pair) => new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken)
        .Claims.Single(claim => claim.Type == NexusStackClaims.Session).Value;

    private static Task<BuildingBlocks.Domain.Result> SendAsync(ServiceProvider provider, ICommand command) =>
        IdentityTestHost.InScopeAsync(provider, scope => scope.GetRequiredService<ISender>().SendAsync(command));

    private static Task<BuildingBlocks.Domain.Result<IReadOnlyList<string>>> PermissionsAsync(ServiceProvider provider, long userId) =>
        IdentityTestHost.InScopeAsync(provider, scope => scope.GetRequiredService<ISender>().QueryAsync(new GetUserPermissionsQuery(userId)));

    private sealed class CommitGate : DbTransactionInterceptor
    {
        private int _armed;

        public bool RejectCommit { get; init; }

        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Arrived.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                if (RejectCommit)
                {
                    throw new InvalidOperationException("Injected commit failure.");
                }
            }

            return result;
        }
    }

    private sealed class PermissionReadGate : DbCommandInterceptor
    {
        private int _armed;

        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("identity.users", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Arrived.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }

            return result;
        }
    }

    public sealed class OtherContext(DbContextOptions<OtherContext> options) : DbContext(options);
}
