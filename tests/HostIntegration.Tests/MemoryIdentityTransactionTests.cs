using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryIdentityTransactionTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("api")]
    public async Task CompetingUniqueValues_CommitOnlyOneAggregateAndItsFacts(string kind)
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var first = app.Services.CreateAsyncScope();
        await using var second = app.Services.CreateAsyncScope();
        var hash = PasswordHash.Create(new Pbkdf2PasswordHasher().Hash("a-strong-password")).Value;
        async Task StageAsync(IServiceProvider provider, int index)
        {
            if (kind == "api")
            {
                await provider.GetRequiredService<IApiResourceRepository>().AddAsync(ApiResource.Create(new ApiResourceId(99100 + index), RoutePattern.Create("/memory/unique").Value, "GET", new MenuId(99103)).Value);
                return;
            }
            var user = User.Register(new UserId(99100 + index), UserName.Create(kind == "name" ? "memory-same-name" : $"memory-unique-{index}").Value, hash, DateTimeOffset.UtcNow);
            user.SetContact(kind == "email" ? EmailAddress.Create("shared@example.test").Value : null,
                kind == "phone" ? PhoneNumber.Create("+8613812345678").Value : null);
            await provider.GetRequiredService<IUserRepository>().AddAsync(user);
        }
        await StageAsync(first.ServiceProvider, 1);
        await StageAsync(second.ServiceProvider, 2);
        await first.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => second.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync());
        var outbox = first.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey);
        Assert.Equal("99101", Assert.Single(await FactsAsync(outbox)).SubjectId);
    }

    [Fact]
    public async Task StandaloneSave_FactBatchFailureCanRetryTrackedChangesWithoutRestaging()
    {
        var serializer = new FailingIdentitySerializer { FailOperation = "menu-granted" };
        await using var baseApp = new MemoryFactCapacityApp("Identity", 2) { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IIntegrationEventSerializer>(serializer)));
        await using var scope = app.Services.CreateAsyncScope();
        var role = Role.Create(new RoleId(99001), RoleCode.Create("retry-fact-batch").Value, RoleName.Create("Private role").Value);
        role.Grant(new MenuId(99002), DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<IRoleRepository>().AddAsync(role);
        var unit = scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey);
        Assert.Empty(await FactsAsync(outbox));
        await using (var observer = app.Services.CreateAsyncScope())
        {
            Assert.Null(await observer.ServiceProvider.GetRequiredService<IRoleRepository>().FindByCodeAsync(role.Code));
        }
        serializer.FailOperation = null;
        await unit.SaveChangesAsync();
        await unit.SaveChangesAsync();
        Assert.Equal(new[] { "created", "menu-granted" }, (await FactsAsync(outbox)).Select(fact => fact.Operation).Order(StringComparer.Ordinal));
        Assert.NotEqual(default, role.CreatedAt);
        await scope.ServiceProvider.GetRequiredService<IRoleRepository>().AddAsync(Role.Create(new RoleId(99003),
            RoleCode.Create("over-fact-capacity").Value, RoleName.Create("Over capacity").Value));
        await Assert.ThrowsAsync<IdentityAuditCapacityException>(() => unit.SaveChangesAsync());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("canceled-before-start")]
    [InlineData("canceled-before-commit")]
    [InlineData("canceled-during-facts")]
    public async Task RejectedOrCanceledCommit_DiscardsStateAndFacts(string mode)
    {
        var serializer = new FailingIdentitySerializer();
        await using var baseApp = new MemoryFactCapacityApp("Identity", 3) { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IIntegrationEventSerializer>(serializer)));
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var created = await sender.SendAsync(new CreateUserCommand("memory-cancellation", "a-strong-password"));
        Assert.True(created.IsSuccess);
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var unit = scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        using var cancellation = new CancellationTokenSource();
        if (mode == "canceled-before-start")
        {
            Assert.True((await users.FindAsync(new UserId(created.Value)))!.Disable(DateTimeOffset.UtcNow).IsSuccess);
            cancellation.Cancel();
        }
        serializer.BeforeSerialize = fact => { if (mode == "canceled-during-facts" && fact.Operation == "disabled") { cancellation.Cancel(); } };
        var attempt = unit.ExecuteInTransactionAsync(async token =>
        {
            var user = (await users.FindAsync(new UserId(created.Value), token))!;
            Assert.True(user.Disable(DateTimeOffset.UtcNow).IsSuccess);
            await unit.SaveChangesAsync(token);
            if (mode == "canceled-before-commit") { cancellation.Cancel(); }
            return false;
        }, _ => mode != "rejected", cancellation.Token);
        if (mode == "rejected") { Assert.False(await attempt); }
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt); }
        var current = (await users.FindAsync(new UserId(created.Value)))!;
        Assert.True(current.IsEnabled);
        Assert.Equal(1, current.Version);
        Assert.Null(current.UpdatedAt);
        Assert.Single(await FactsAsync(scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)));
        serializer.BeforeSerialize = null;
        Assert.True((await sender.SendAsync(new LoginCommand("memory-cancellation", "a-strong-password"))).IsSuccess);
        Assert.Equal(3, (await FactsAsync(scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey))).Length);
        Assert.Equal(IdentityAuditCapacityException.Exhausted,
            (await sender.SendAsync(new CreateUserCommand("memory-cancellation-excess", "a-strong-password"))).Error);
    }

    [Fact]
    public async Task OwnedCollections_AreIsolated_AndFactsDescribeNetCommittedMembershipAndNodeChanges()
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        var role = Role.Create(new RoleId(98001), RoleCode.Create("memory-collections").Value, RoleName.Create("Private role").Value);
        role.ReplaceGrants([new MenuId(98002), new MenuId(98003)], DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<IRoleRepository>().AddAsync(role);
        await unit.SaveChangesAsync();
        role.ReplaceGrants([new MenuId(98003), new MenuId(98004)], DateTimeOffset.UtcNow);
        role.Rename(RoleName.Create("Private rename").Value);
        await using (var observer = app.Services.CreateAsyncScope())
        {
            var original = Assert.Single(await observer.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([role.Id]));
            Assert.Equal("Private role", original.Name.Value);
            Assert.Equal(new long[] { 98002, 98003 }, original.GrantedMenuIds.Select(menu => menu.Value));
        }
        await unit.SaveChangesAsync();
        role.ReplaceGrants([new MenuId(98003), new MenuId(98004)], DateTimeOffset.UtcNow);
        await unit.SaveChangesAsync();
        var tree = MenuTree.Create(new MenuTreeId(98010));
        tree.AddRoot(new MenuId(98011), MenuTitle.Create("Private root").Value);
        tree.AddRoot(new MenuId(98012), MenuTitle.Create("Private other root").Value);
        tree.AddChild(new MenuId(98011), new MenuId(98013), MenuTitle.Create("Private child").Value);
        tree.AddChild(new MenuId(98013), new MenuId(98014), MenuTitle.Create("Private descendant").Value);
        await scope.ServiceProvider.GetRequiredService<IMenuTreeRepository>().AddAsync(tree);
        await unit.SaveChangesAsync();
        tree.Move(new MenuId(98013), new MenuId(98012));
        tree.Update(new MenuId(98013), MenuTitle.Create("Private child rename").Value, 9);
        await using (var observer = app.Services.CreateAsyncScope())
        {
            var original = (await observer.ServiceProvider.GetRequiredService<IMenuTreeRepository>().FindAsync())!;
            Assert.Equal(98011, original.Find(new MenuId(98013))!.ParentId!.Value);
            Assert.Equal("Private child", original.Find(new MenuId(98013))!.Title.Value);
        }
        await unit.SaveChangesAsync();
        Assert.True(tree.Remove(new MenuId(98014)).IsSuccess);
        Assert.True(tree.Remove(new MenuId(98013)).IsSuccess);
        await unit.SaveChangesAsync();
        var facts = await FactsAsync(scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey));
        var roleFacts = facts.Where(fact => fact.SubjectType == "role").ToArray();
        Assert.Equal(new[] { "created", "menu-granted", "menu-granted", "menu-granted", "menu-revoked", "renamed" }, roleFacts.Select(fact => fact.Operation).Order(StringComparer.Ordinal));
        Assert.Equal(98002, Assert.Single(roleFacts, fact => fact.Operation == "menu-revoked").RelatedSubject!.Id);
        var moves = facts.Where(fact => fact.Operation is "node-moved" or "node-ancestry-changed").ToArray();
        Assert.Equal(2, moves.Length);
        Assert.Equal(98013, Assert.Single(moves, fact => fact.Operation == "node-moved").RelatedSubject!.Id);
        Assert.Equal(98014, Assert.Single(moves, fact => fact.Operation == "node-ancestry-changed").RelatedSubject!.Id);
        Assert.Equal(2, facts.Count(fact => fact.Operation == "node-removed"));
        Assert.Single(facts, fact => fact.Operation == "node-renamed");
        Assert.Single(facts, fact => fact.Operation == "node-reordered");
        Assert.All(facts, fact => Assert.DoesNotContain("Private", new SystemTextJsonIntegrationEventSerializer().Serialize(fact), StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrackedChanges_AfterSaveRemainTracked_AndStaleWritersCannotOverwriteTheWinner()
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var first = app.Services.CreateAsyncScope();
        var sender = first.ServiceProvider.GetRequiredService<ISender>();
        var created = await sender.SendAsync(new CreateUserCommand("memory-version", "a-strong-password"));
        Assert.True(created.IsSuccess);
        var repository = first.ServiceProvider.GetRequiredService<IUserRepository>();
        var unit = first.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        var user = (await repository.FindAsync(new UserId(created.Value)))!;
        user.AssignRole(new RoleId(83001), DateTimeOffset.UtcNow);
        await unit.SaveChangesAsync();
        user.AssignRole(new RoleId(83002), DateTimeOffset.UtcNow);
        await unit.SaveChangesAsync();
        await using var second = app.Services.CreateAsyncScope();
        var other = (await second.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(user.Id))!;
        Assert.Equal(new long[] { 83001, 83002 }, other.RoleIds.Select(role => role.Value));
        Assert.Equal(3, other.Version);
        user.RevokeRole(new RoleId(83001), DateTimeOffset.UtcNow);
        await unit.SaveChangesAsync();
        other.RevokeRole(new RoleId(83002), DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => second.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync());
        var reloaded = (await second.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(user.Id))!;
        Assert.Equal(83002, Assert.Single(reloaded.RoleIds).Value);
        var outbox = second.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey);
        Assert.Equal(4, (await FactsAsync(outbox)).Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task EnableAfterFailedLogins_DoesNotInventSuccessfulLogin(int failures)
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var setup = app.Services.CreateAsyncScope();
        var sender = setup.ServiceProvider.GetRequiredService<ISender>();
        var created = await sender.SendAsync(new CreateUserCommand("enable-audit-user", "a-strong-password"));
        Assert.True(created.IsSuccess);
        Assert.True((await sender.SendAsync(new LoginCommand("enable-audit-user", "a-strong-password"))).IsSuccess);
        for (var attempt = 0; attempt < failures; attempt++)
        {
            Assert.True((await sender.SendAsync(new LoginCommand("enable-audit-user", "wrong-password"))).IsFailure);
        }
        var before = await setup.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
        Assert.NotNull(before);
        Assert.Equal(failures, before.FailedLoginCount);
        Assert.Equal(failures == 5, before.LockedUntil is not null);
        var lastLogin = before.LastLoginAt;
        var version = before.Version;
        Assert.NotNull(lastLogin);
        Assert.True((await sender.SendAsync(new SetUserEnabledCommand(created.Value, version, false))).IsSuccess);
        Assert.True((await sender.SendAsync(new SetUserEnabledCommand(created.Value, version + 1, true))).IsSuccess);
        await using var read = app.Services.CreateAsyncScope();
        var enabled = await read.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
        Assert.NotNull(enabled);
        Assert.True(enabled.IsEnabled);
        Assert.Equal(0, enabled.FailedLoginCount);
        Assert.Null(enabled.LockedUntil);
        Assert.Equal(lastLogin, enabled.LastLoginAt);
        Assert.Equal(failures + 4, enabled.Version);
        var facts = (await FactsAsync(read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)))
            .Where(fact => fact.SubjectType == "user").OrderBy(fact => fact.Version).ThenBy(fact => fact.Operation, StringComparer.Ordinal).ToArray();
        var rejected = Enumerable.Range(1, failures).Select(attempt => attempt == 5 ? "login-locked" : "login-failed");
        Assert.Equal(new[] { "created", "login-succeeded" }.Concat(rejected).Concat(["disabled", "sessions-revoked", "enabled"]), facts.Select(fact => fact.Operation));
    }

    [Fact]
    public async Task SuccessfulLoginAtTheSameTimestamp_RecordsTheCommittedFailureReset()
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var setup = app.Services.CreateAsyncScope();
        var created = await setup.ServiceProvider.GetRequiredService<ISender>().SendAsync(new CreateUserCommand("same-time-memory", "a-strong-password"));
        Assert.True(created.IsSuccess);
        var at = new DateTimeOffset(2026, 10, 3, 1, 0, 0, TimeSpan.Zero);
        for (var step = 0; step < 3; step++)
        {
            await using var change = app.Services.CreateAsyncScope();
            var user = await change.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
            Assert.NotNull(user);
            if (step == 1) { user.RecordFailedLogin(at, LockoutPolicy.Default); }
            else { user.RecordSuccessfulLogin(at); }
            await change.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using var read = app.Services.CreateAsyncScope();
        var reloaded = await read.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
        Assert.NotNull(reloaded);
        Assert.Equal(0, reloaded.FailedLoginCount);
        Assert.Equal(at, reloaded.LastLoginAt);
        var facts = (await FactsAsync(read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)))
            .OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "created", "login-succeeded", "login-failed", "login-succeeded" }, facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, facts.Select(fact => fact.Version));
    }

    [Fact]
    public async Task SecurityRejection_CommitsItsFact_WhileFactFailureRollsBackAndAllowsRetry()
    {
        var serializer = new FailingIdentitySerializer();
        await using var baseApp = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IIntegrationEventSerializer>(serializer)));
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey);
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var name = UserName.Create("memory-fact-user").Value;
        serializer.FailOperation = "created";
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new CreateUserCommand(name.Value, "a-strong-password")));
        Assert.Null(await users.FindByUserNameAsync(name));
        Assert.Empty(await FactsAsync(outbox));
        serializer.FailOperation = null;
        Assert.True((await sender.SendAsync(new CreateUserCommand(name.Value, "a-strong-password"))).IsSuccess);
        Assert.True((await sender.SendAsync(new LoginCommand(name.Value, "wrong-password"))).IsFailure);
        var failedUser = (await users.FindByUserNameAsync(name))!;
        Assert.Equal(1, failedUser.FailedLoginCount);
        Assert.NotEqual(default, failedUser.CreatedAt);
        Assert.NotNull(failedUser.UpdatedAt);
        Assert.Equal(new[] { "created", "login-failed" }, (await FactsAsync(outbox)).Select(fact => fact.Operation).Order(StringComparer.Ordinal));
        serializer.FailOperation = "issued";
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new LoginCommand(name.Value, "a-strong-password")));
        var afterFailure = (await users.FindByUserNameAsync(name))!;
        Assert.Equal(1, afterFailure.FailedLoginCount);
        Assert.Null(afterFailure.LastLoginAt);
        Assert.Equal(2, (await FactsAsync(outbox)).Length);
        serializer.FailOperation = null;
        Assert.True((await sender.SendAsync(new LoginCommand(name.Value, "a-strong-password"))).IsSuccess);
        var facts = await FactsAsync(outbox);
        Assert.Equal(new[] { "created", "issued", "login-failed", "login-succeeded" }, facts.Select(fact => fact.Operation).Order(StringComparer.Ordinal));
        Assert.All(facts, fact =>
        {
            Assert.NotNull(fact.Execution);
            Assert.DoesNotContain(name.Value, new SystemTextJsonIntegrationEventSerializer().Serialize(fact), StringComparison.Ordinal);
        });
        Assert.Equal(0, (await users.FindByUserNameAsync(name))!.FailedLoginCount);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("menu-tree")]
    [InlineData("api-resource")]
    [InlineData("refresh-token")]
    public async Task NewAggregate_RemainsPrivateUntilTheTransactionCommits(string kind)
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        var code = RoleCode.Create("memory-transaction").Value;
        var hash = TokenHash.Create(new string('a', 64)).Value;
        async Task AddAsync(IServiceProvider provider, CancellationToken token)
        {
            switch (kind)
            {
                case "role":
                    await provider.GetRequiredService<IRoleRepository>().AddAsync(Role.Create(new RoleId(97001), code, RoleName.Create("Memory role").Value), token);
                    break;
                case "menu-tree":
                    await provider.GetRequiredService<IMenuTreeRepository>().AddAsync(MenuTree.Create(new MenuTreeId(97002)), token);
                    break;
                case "api-resource":
                    await provider.GetRequiredService<IApiResourceRepository>().AddAsync(ApiResource.Create(new ApiResourceId(97003), RoutePattern.Create("/memory/probe").Value, "GET", new MenuId(97004)).Value, token);
                    break;
                case "refresh-token":
                    await provider.GetRequiredService<IRefreshTokenRepository>().AddAsync(RefreshToken.Issue(new RefreshTokenId(97005), new UserId(97006), hash, DateTimeOffset.UtcNow, TimeSpan.FromDays(1)).Value, token);
                    break;
                default: throw new InvalidOperationException("Unknown test case");
            }
        }
        async Task<bool> ExistsAsync(IServiceProvider provider, CancellationToken token) => kind switch
        {
            "role" => await provider.GetRequiredService<IRoleRepository>().FindByCodeAsync(code, token) is not null,
            "menu-tree" => await provider.GetRequiredService<IMenuTreeRepository>().FindAsync(token) is not null,
            "api-resource" => (await provider.GetRequiredService<IApiResourceRepository>().FindByMenuIdsAsync(new HashSet<MenuId> { new(97004) }, token)).Count != 0,
            "refresh-token" => await provider.GetRequiredService<IRefreshTokenRepository>().FindByHashAsync(hash, token) is not null,
            _ => throw new InvalidOperationException("Unknown test case"),
        };
        await using var writer = app.Services.CreateAsyncScope();
        var unit = writer.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteInTransactionAsync<int>(async token =>
        {
            await AddAsync(writer.ServiceProvider, token);
            await unit.SaveChangesAsync(token);
            await using var observer = app.Services.CreateAsyncScope();
            Assert.False(await ExistsAsync(observer.ServiceProvider, token));
            throw new InvalidOperationException("Injected operation failure after save");
        }));
        Assert.False(await ExistsAsync(writer.ServiceProvider, CancellationToken.None));
        await unit.ExecuteInTransactionAsync(async token =>
        {
            await AddAsync(writer.ServiceProvider, token);
            await unit.SaveChangesAsync(token);
            return true;
        });
        await using var verify = app.Services.CreateAsyncScope();
        Assert.True(await ExistsAsync(verify.ServiceProvider, CancellationToken.None));
        var facts = await FactsAsync(verify.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey));
        Assert.Equal(kind, Assert.Single(facts).SubjectType);
    }

    [Fact]
    public async Task WorkingChanges_AreInvisibleUntilCommit_AndFailureDiscardsThem()
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        long id;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<ISender>()
                .SendAsync(new CreateUserCommand("memory-isolation", "a-strong-password"));
            Assert.True(created.IsSuccess);
            id = created.Value;
        }
        await using var writer = app.Services.CreateAsyncScope();
        var users = writer.ServiceProvider.GetRequiredService<IUserRepository>();
        var unit = writer.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteInTransactionAsync<int>(async token =>
        {
            var user = (await users.FindAsync(new UserId(id), token))!;
            Assert.True(user.Disable(DateTimeOffset.UtcNow).IsSuccess);
            await unit.SaveChangesAsync(token);
            await using var observer = app.Services.CreateAsyncScope();
            var committed = (await observer.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(id), token))!;
            Assert.True(committed.IsEnabled);
            Assert.Equal(1, committed.Version);
            throw new InvalidOperationException("Injected operation failure after save");
        }));
        var reloaded = (await users.FindAsync(new UserId(id)))!;
        Assert.True(reloaded.IsEnabled);
        Assert.Equal(1, reloaded.Version);
        Assert.True(reloaded.Disable(DateTimeOffset.UtcNow).IsSuccess);
        await unit.SaveChangesAsync();
        await using var verify = app.Services.CreateAsyncScope();
        var saved = (await verify.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(id)))!;
        Assert.False(saved.IsEnabled);
        Assert.Equal(2, saved.Version);
    }

    private static async Task<IdentityEntityCommittedV1[]> FactsAsync(IOutboxStore outbox) =>
        (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Select(item => new SystemTextJsonIntegrationEventSerializer().Deserialize<IdentityEntityCommittedV1>(item.Payload)).ToArray();

    private sealed class FailingIdentitySerializer : IIntegrationEventSerializer
    {
        private readonly SystemTextJsonIntegrationEventSerializer _inner = new();
        public string? FailOperation { get; set; }
        public Action<IdentityEntityCommittedV1>? BeforeSerialize { get; set; }
        public string Serialize(IntegrationEvent integrationEvent)
        {
            if (integrationEvent is IdentityEntityCommittedV1 observed) { BeforeSerialize?.Invoke(observed); }
            if (integrationEvent is IdentityEntityCommittedV1 fact && fact.Operation == FailOperation) { throw new InvalidOperationException("Injected fact serialization failure"); }
            return _inner.Serialize(integrationEvent);
        }
        public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => _inner.Deserialize<TEvent>(payload);
    }
}
