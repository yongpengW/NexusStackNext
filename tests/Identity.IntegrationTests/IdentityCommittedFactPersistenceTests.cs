using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.Identity.IntegrationTests;

[Collection(IdentityDatabaseGroup.Name)]
public sealed class IdentityCommittedFactPersistenceTests(IdentityDatabaseFixture fixture)
{
    [PostgresFact]
    public async Task SerializationInterruptedMidBatch_CommitsNothing_AndSameScopeRetryDoesNotDuplicateFacts()
    {
        await fixture.ResetAsync();
        var serializer = new FailSecondFactOnceSerializer();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString,
            services => services.AddSingleton<IIntegrationEventSerializer>(serializer));
        await using var scope = provider.CreateAsyncScope();
        var tree = MenuTree.Create(new MenuTreeId(75700));
        Assert.True(tree.AddRoot(new MenuId(75701), MenuTitle.Create("Private node").Value).IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IMenuTreeRepository>().AddAsync(tree);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.SaveChangesAsync());
        await using (var failedRead = provider.CreateAsyncScope())
        {
            Assert.Null(await failedRead.ServiceProvider.GetRequiredService<IMenuTreeRepository>().FindAsync());
            Assert.Empty(await failedRead.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
                .ReadPendingAsync(100, DateTimeOffset.UtcNow));
        }
        await unitOfWork.SaveChangesAsync();
        await unitOfWork.SaveChangesAsync();
        await using var read = provider.CreateAsyncScope();
        Assert.NotNull(await read.ServiceProvider.GetRequiredService<IMenuTreeRepository>().FindAsync());
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).ToArray();
        Assert.Equal(new[] { "created", "node-added" }, facts.Select(fact => fact.Operation).Order(StringComparer.Ordinal));
        Assert.All(facts, fact => Assert.Equal(2, fact.Version));
    }

    private sealed class FailSecondFactOnceSerializer : IIntegrationEventSerializer
    {
        private readonly SystemTextJsonIntegrationEventSerializer _inner = new();
        private int _facts;
        public string Serialize(IntegrationEvent integrationEvent)
        {
            if (integrationEvent is IdentityEntityCommittedV1 && ++_facts == 2) { throw new InvalidOperationException("Injected second fact serialization failure."); }
            return _inner.Serialize(integrationEvent);
        }
        public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => _inner.Deserialize<TEvent>(payload);
    }

    [PostgresFact]
    public async Task ReplacingANodeBeforeCommit_RecordsOnlyItsNetContentChange()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var title = MenuTitle.Create("Private original node").Value;
        await using (var create = provider.CreateAsyncScope())
        {
            var tree = MenuTree.Create(new MenuTreeId(75600));
            Assert.True(tree.AddRoot(new MenuId(75601), title).IsSuccess);
            await create.ServiceProvider.GetRequiredService<IMenuTreeRepository>().AddAsync(tree);
            await create.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        for (var step = 0; step < 2; step++)
        {
            await using var replace = provider.CreateAsyncScope();
            var tree = await replace.ServiceProvider.GetRequiredService<IMenuTreeRepository>().FindAsync();
            Assert.NotNull(tree);
            Assert.True(tree.Remove(new MenuId(75601)).IsSuccess);
            Assert.True(tree.AddRoot(new MenuId(75601), step == 0 ? title : MenuTitle.Create("Private replacement node").Value, step == 0 ? 0 : 3).IsSuccess);
            await replace.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using var read = provider.CreateAsyncScope();
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload))
            .OrderBy(fact => fact.Version).ThenBy(fact => fact.Operation, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "created", "node-added", "node-renamed", "node-reordered" }, facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 2, 2, 6, 6 }, facts.Select(fact => fact.Version));
        Assert.All(facts.Skip(1), fact => Assert.Equal(new IdentitySubjectReference("menu", 75601), fact.RelatedSubject));
        Assert.All(pending, entry => Assert.DoesNotContain("Private", entry.Payload, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task MovingASubtree_RecordsTheMovedNodeAndAffectedDescendant_WithTheOwningVersion()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        await using (var create = provider.CreateAsyncScope())
        {
            var tree = MenuTree.Create(new MenuTreeId(75500));
            var title = MenuTitle.Create("Private subtree title").Value;
            Assert.True(tree.AddRoot(new MenuId(75501), title).IsSuccess);
            Assert.True(tree.AddRoot(new MenuId(75502), title).IsSuccess);
            Assert.True(tree.AddChild(new MenuId(75501), new MenuId(75503), title).IsSuccess);
            Assert.True(tree.AddChild(new MenuId(75503), new MenuId(75504), title).IsSuccess);
            await create.ServiceProvider.GetRequiredService<IMenuTreeRepository>().AddAsync(tree);
            await create.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var move = provider.CreateAsyncScope();
            var tree = await move.ServiceProvider.GetRequiredService<IMenuTreeRepository>().FindAsync();
            Assert.NotNull(tree);
            Assert.True(tree.Move(new MenuId(75503), new MenuId(75502)).IsSuccess);
            await move.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using var read = provider.CreateAsyncScope();
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).ToArray();
        var initial = facts.Where(fact => fact.Version == 5).ToArray();
        Assert.Equal(5, initial.Length);
        Assert.Single(initial, fact => fact.Operation == "created" && fact.RelatedSubject is null);
        Assert.Equal(new long[] { 75501, 75502, 75503, 75504 }, initial.Where(fact => fact.Operation == "node-added")
            .Select(fact => fact.RelatedSubject!.Id).Order());
        var moved = facts.Where(fact => fact.Version == 6).ToArray();
        Assert.Equal(2, moved.Length);
        Assert.Equal(new IdentitySubjectReference("menu", 75503), Assert.Single(moved, fact => fact.Operation == "node-moved").RelatedSubject);
        Assert.Equal(new IdentitySubjectReference("menu", 75504), Assert.Single(moved, fact => fact.Operation == "node-ancestry-changed").RelatedSubject);
        Assert.Equal(7, facts.Length);
        Assert.All(facts, fact => { Assert.Equal("menu-tree", fact.SubjectType); Assert.Equal("75500", fact.SubjectId); });
        Assert.All(pending, entry => Assert.DoesNotContain("Private", entry.Payload, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task EnableAfterFailedLogins_DoesNotInventSuccessfulLogin()
    {
        foreach (var failures in new[] { 1, 5 })
        {
            await fixture.ResetAsync();
            await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
            var created = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
                .SendAsync(new CreateUserCommand("enable-audit-user", "a-strong-password")));
            Assert.True(created.IsSuccess);
            Assert.True((await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
                .SendAsync(new LoginCommand("enable-audit-user", "a-strong-password")))).IsSuccess);
            for (var attempt = 0; attempt < failures; attempt++)
            {
                Assert.True((await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
                    .SendAsync(new LoginCommand("enable-audit-user", "wrong-password")))).IsFailure);
            }
            await using var beforeScope = provider.CreateAsyncScope();
            var before = await beforeScope.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
            Assert.NotNull(before);
            Assert.Equal(failures, before.FailedLoginCount);
            Assert.Equal(failures == 5, before.LockedUntil is not null);
            Assert.NotNull(before.LastLoginAt);
            Assert.True((await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
                .SendAsync(new SetUserEnabledCommand(created.Value, before.Version, false)))).IsSuccess);
            Assert.True((await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
                .SendAsync(new SetUserEnabledCommand(created.Value, before.Version + 1, true)))).IsSuccess);
            await using var read = provider.CreateAsyncScope();
            var enabled = await read.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
            Assert.NotNull(enabled);
            Assert.True(enabled.IsEnabled);
            Assert.Equal(0, enabled.FailedLoginCount);
            Assert.Null(enabled.LockedUntil);
            Assert.Equal(before.LastLoginAt, enabled.LastLoginAt);
            Assert.Equal(failures + 4, enabled.Version);
            var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
            var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
                .ReadPendingAsync(100, DateTimeOffset.UtcNow);
            var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload))
                .Where(fact => fact.SubjectType == "user").OrderBy(fact => fact.Version).ThenBy(fact => fact.Operation, StringComparer.Ordinal).ToArray();
            var rejected = Enumerable.Range(1, failures).Select(attempt => attempt == 5 ? "login-locked" : "login-failed");
            Assert.Equal(new[] { "created", "login-succeeded" }.Concat(rejected).Concat(["disabled", "sessions-revoked", "enabled"]), facts.Select(fact => fact.Operation));
        }
    }

    [PostgresFact]
    public async Task SuccessfulLoginAtTheSameTimestamp_RecordsTheCommittedFailureReset()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var created = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateUserCommand("same-time-login", "private-same-time-password")));
        Assert.True(created.IsSuccess);
        var at = new DateTimeOffset(2026, 10, 3, 1, 0, 0, TimeSpan.Zero);
        for (var step = 0; step < 3; step++)
        {
            await using var change = provider.CreateAsyncScope();
            var user = await change.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
            Assert.NotNull(user);
            if (step == 1) { user.RecordFailedLogin(at, LockoutPolicy.Default); }
            else { user.RecordSuccessfulLogin(at); }
            await change.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using var read = provider.CreateAsyncScope();
        var reloaded = await read.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
        Assert.NotNull(reloaded);
        Assert.Equal(0, reloaded.FailedLoginCount);
        Assert.Equal(at, reloaded.LastLoginAt);
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "created", "login-succeeded", "login-failed", "login-succeeded" }, facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, facts.Select(fact => fact.Version));
    }

    [PostgresFact]
    public async Task RemovingAndReaddingARoleBeforeCommit_DoesNotInventAnAssignmentChange()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var created = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateUserCommand("unchanged-role-user", "private-unchanged-password")));
        Assert.True(created.IsSuccess);
        var at = DateTimeOffset.UtcNow;
        await using (var assign = provider.CreateAsyncScope())
        {
            var user = await assign.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
            Assert.NotNull(user);
            Assert.True(user.AssignRole(new RoleId(75400), at).IsSuccess);
            await assign.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using (var replace = provider.CreateAsyncScope())
        {
            var user = await replace.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
            Assert.NotNull(user);
            Assert.True(user.RevokeRole(new RoleId(75400), at).IsSuccess);
            Assert.True(user.AssignRole(new RoleId(75400), at).IsSuccess);
            await replace.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using var read = provider.CreateAsyncScope();
        var reloaded = await read.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
        Assert.NotNull(reloaded);
        Assert.Equal(4, reloaded.Version);
        Assert.Equal(75400, Assert.Single(reloaded.RoleIds).Value);
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "created", "role-assigned" }, facts.Select(fact => fact.Operation));
    }

    [PostgresFact]
    public async Task ReplacingOverlappingPermissions_RecordsOnlyTheMembershipDifference()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var at = DateTimeOffset.UtcNow;
        await using (var create = provider.CreateAsyncScope())
        {
            var role = Role.Create(new RoleId(75300), RoleCode.Create("overlap-fact-role").Value, RoleName.Create("Overlap role").Value);
            Assert.True(role.ReplaceGrants([new MenuId(75301), new MenuId(75302)], at).IsSuccess);
            await create.ServiceProvider.GetRequiredService<IRoleRepository>().AddAsync(role);
            await create.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using (var update = provider.CreateAsyncScope())
        {
            var role = Assert.Single(await update.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(75300)]));
            Assert.True(role.ReplaceGrants([new MenuId(75302), new MenuId(75303)], at).IsSuccess);
            await update.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using var read = provider.CreateAsyncScope();
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var changed = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).Where(fact => fact.Version == 3).ToArray();
        Assert.Equal(2, changed.Length);
        Assert.Equal(new IdentitySubjectReference("menu", 75301), Assert.Single(changed, fact => fact.Operation == "menu-revoked").RelatedSubject);
        Assert.Equal(new IdentitySubjectReference("menu", 75303), Assert.Single(changed, fact => fact.Operation == "menu-granted").RelatedSubject);
    }

    [PostgresFact]
    public async Task CreatingAggregatesWithInitialPermissions_RecordsEveryCommittedRelationship()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var at = DateTimeOffset.UtcNow;
        await using (var createRole = provider.CreateAsyncScope())
        {
            var role = Role.Create(new RoleId(75100), RoleCode.Create("initial-fact-role").Value, RoleName.Create("Private initial role").Value);
            Assert.True(role.Grant(new MenuId(75101), at).IsSuccess);
            Assert.True(role.Grant(new MenuId(75102), at).IsSuccess);
            await createRole.ServiceProvider.GetRequiredService<IRoleRepository>().AddAsync(role);
            await createRole.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using (var createUser = provider.CreateAsyncScope())
        {
            var hash = createUser.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash("private-initial-password");
            var user = User.Register(new UserId(75200), UserName.Create("initial-fact-user").Value, PasswordHash.Create(hash).Value, at);
            Assert.True(user.AssignRole(new RoleId(75100), at).IsSuccess);
            await createUser.ServiceProvider.GetRequiredService<IUserRepository>().AddAsync(user);
            await createUser.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using var read = provider.CreateAsyncScope();
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).ToArray();
        Assert.Equal(5, facts.Length);
        var roleFacts = facts.Where(fact => fact.SubjectType == "role").ToArray();
        Assert.Equal(3, roleFacts.Length);
        Assert.Single(roleFacts, fact => fact.Operation == "created" && fact.RelatedSubject is null);
        Assert.Equal(new long[] { 75101, 75102 }, roleFacts.Where(fact => fact.Operation == "menu-granted")
            .Select(fact => fact.RelatedSubject!.Id).Order());
        Assert.All(roleFacts, fact => { Assert.Equal("75100", fact.SubjectId); Assert.Equal(3, fact.Version); });
        var userFacts = facts.Where(fact => fact.SubjectType == "user").ToArray();
        Assert.Equal(2, userFacts.Length);
        Assert.Single(userFacts, fact => fact.Operation == "created" && fact.RelatedSubject is null);
        Assert.Equal(new IdentitySubjectReference("role", 75100), Assert.Single(userFacts, fact => fact.Operation == "role-assigned").RelatedSubject);
        Assert.All(userFacts, fact => { Assert.Equal("75200", fact.SubjectId); Assert.Equal(2, fact.Version); });
    }

    [PostgresFact]
    public async Task ApiRegistration_RecordsTheCommittedResource_AndRejectsInvalidInputWithoutAFact()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var menu = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateMenuCommand("Audit resource menu", 1, null)));
        Assert.True(menu.IsSuccess);
        var created = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateApiResourceCommand("/private-audit-resource/{id}", "GET", menu.Value.MenuId)));
        Assert.True(created.IsSuccess);
        var rejected = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateApiResourceCommand("", "GET", null)));
        Assert.True(rejected.IsFailure);
        await using var scope = provider.CreateAsyncScope();
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        Assert.Equal(3, pending.Count); // MenuTree creation and node addition, plus the one accepted API registration.
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        Assert.Equal(new[] { "created", "node-added" }, pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload))
            .Where(fact => fact.SubjectType == "menu-tree").Select(fact => fact.Operation).Order(StringComparer.Ordinal));
        var entry = Assert.Single(pending, entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload).SubjectType == "api-resource");
        var fact = serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload);
        Assert.Equal("api-resource", fact.SubjectType);
        Assert.Equal(created.Value.ApiResourceId.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.SubjectId);
        Assert.Equal("registered", fact.Operation);
        Assert.Equal(1, fact.Version);
        Assert.Equal(new IdentitySubjectReference("menu", menu.Value.MenuId), fact.RelatedSubject);
        Assert.DoesNotContain("private-audit-resource", entry.Payload, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task MenuChanges_RecordTheOwningTreeCommit_IncludingRemovingTheLastNode()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        await using (var create = provider.CreateAsyncScope())
        {
            await create.ServiceProvider.GetRequiredService<IMenuTreeRepository>().AddAsync(MenuTree.Create(new MenuTreeId(74000)));
            await create.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        Action<MenuTree>[] mutations =
        [
            tree => tree.AddRoot(new MenuId(74001), MenuTitle.Create("Private first").Value),
            tree => tree.AddRoot(new MenuId(74002), MenuTitle.Create("Private second").Value),
            tree => tree.AddChild(new MenuId(74001), new MenuId(74003), MenuTitle.Create("Private child").Value),
            tree => tree.Update(new MenuId(74001), MenuTitle.Create("Private renamed").Value, 2),
            tree => tree.Move(new MenuId(74003), new MenuId(74002)),
            tree => tree.Remove(new MenuId(74003)),
            tree => tree.Remove(new MenuId(74001)),
            tree => tree.Remove(new MenuId(74002)),
        ];
        foreach (var mutate in mutations)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var scope = provider.CreateAsyncScope();
                var tree = await scope.ServiceProvider.GetRequiredService<IMenuTreeRepository>().FindAsync();
                Assert.NotNull(tree);
                mutate(tree);
                await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
            }
        }
        await using var read = provider.CreateAsyncScope();
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).OrderBy(fact => fact.Version).ThenBy(fact => fact.Operation, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "created", "node-added", "node-added", "node-added", "node-renamed", "node-reordered", "node-moved", "node-removed", "node-removed", "node-removed" },
            facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 5, 6, 7, 8, 9 }, facts.Select(fact => fact.Version));
        Assert.Null(facts[0].RelatedSubject);
        Assert.Equal(new long[] { 74001, 74002, 74003, 74001, 74001, 74003, 74003, 74001, 74002 }, facts.Skip(1).Select(fact => fact.RelatedSubject!.Id));
        Assert.All(facts.Skip(1), fact => Assert.Equal("menu", fact.RelatedSubject?.Type));
        Assert.All(facts, fact => { Assert.Equal("menu-tree", fact.SubjectType); Assert.Equal("74000", fact.SubjectId); });
        Assert.All(pending, entry => Assert.DoesNotContain("Private", entry.Payload, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task RoleMaintenance_RecordsNamePlatformAndRemovedPermissions_WithoutNoOpFacts()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var created = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateRoleCommand("maintenance-fact-role", "Private original name")));
        Assert.True(created.IsSuccess);
        var at = DateTimeOffset.UtcNow;
        Action<Role>[] mutations =
        [
            role => role.Rename(RoleName.Create("Private new name").Value),
            role => role.ChangePlatforms(NexusStackNext.Identity.Domain.Platform.Admin),
            role => role.Grant(new MenuId(701), at),
            role => role.Revoke(new MenuId(701), at),
            role => role.ReplaceGrants([new MenuId(702), new MenuId(703)], at),
            role => role.ReplaceGrants([], at),
        ];
        foreach (var mutate in mutations)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var scope = provider.CreateAsyncScope();
                var role = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(created.Value)]));
                mutate(role);
                await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
            }
        }
        await using var read = provider.CreateAsyncScope();
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "created", "renamed", "platforms-changed", "menu-granted", "menu-revoked", "menu-granted", "menu-granted", "menu-revoked", "menu-revoked" },
            facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 6, 7, 7 }, facts.Select(fact => fact.Version));
        Assert.Equal(new long[] { 701, 701, 702, 703, 702, 703 }, facts.Where(fact => fact.RelatedSubject is not null)
            .OrderBy(fact => fact.Version).ThenBy(fact => fact.RelatedSubject!.Id).Select(fact => fact.RelatedSubject!.Id));
        Assert.All(facts.Where(fact => fact.RelatedSubject is not null), fact => Assert.Equal("menu", fact.RelatedSubject!.Type));
        Assert.All(facts, fact => Assert.Equal("role", fact.SubjectType));
        Assert.All(pending, entry => Assert.DoesNotContain("Private", entry.Payload, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task UserMaintenance_RecordsCommittedChanges_AndIgnoresUnchangedValues()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var created = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateUserCommand("maintenance-fact-user", "private-maintenance-password")));
        Assert.True(created.IsSuccess);
        var email = EmailAddress.Create("private-contact@example.test").Value;
        var passwordHash = provider.GetRequiredService<IPasswordHasher>().Hash("private-replacement-password");
        var password = PasswordHash.Create(passwordHash).Value;
        var at = DateTimeOffset.UtcNow;
        Action<User>[] mutations =
        [
            user => user.SetContact(email, null),
            user => user.SetContact(null, null),
            user => user.ChangePassword(password, at),
            user => user.Disable(at),
            user => user.Enable(at),
            user => user.AssignRole(new RoleId(701), at),
            user => user.RevokeRole(new RoleId(701), at),
        ];
        foreach (var mutate in mutations)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var scope = provider.CreateAsyncScope();
                var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(created.Value));
                Assert.NotNull(user);
                mutate(user);
                await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
            }
        }
        await using var read = provider.CreateAsyncScope();
        var serializer = read.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload))
            .OrderBy(fact => fact.Version).ThenBy(fact => fact.Operation, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "created", "contact-changed", "contact-changed", "password-changed", "sessions-revoked", "disabled", "sessions-revoked", "enabled", "role-assigned", "role-revoked" },
            facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2, 3, 4, 4, 5, 5, 6, 7, 8 }, facts.Select(fact => fact.Version));
        Assert.All(facts, fact => Assert.Equal("user", fact.SubjectType));
        Assert.Equal(new IdentitySubjectReference("role", 701), facts[^2].RelatedSubject);
        Assert.Equal(new IdentitySubjectReference("role", 701), facts[^1].RelatedSubject);
        Assert.All(pending, entry => Assert.DoesNotContain("private-", entry.Payload, StringComparison.Ordinal));
        Assert.All(pending, entry => Assert.False(entry.Payload.Contains(passwordHash, StringComparison.Ordinal)));
    }

    [PostgresFact]
    public async Task RevokingToken_RecordsOnlyTheFirstCommittedRevocation_WithoutTheFreeTextReason()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        Assert.True((await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateUserCommand("revoke-fact-user", "private-revoke-password")))).IsSuccess);
        var login = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new LoginCommand("revoke-fact-user", "private-revoke-password")));
        Assert.True(login.IsSuccess);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var mutation = provider.CreateAsyncScope();
            var services = mutation.ServiceProvider;
            var hash = TokenHash.Create(services.GetRequiredService<ISecretHasher>().Hash(login.Value.Tokens.RefreshToken)).Value;
            var token = await services.GetRequiredService<IRefreshTokenRepository>().FindByHashAsync(hash);
            Assert.NotNull(token);
            Assert.True(token.Revoke(DateTimeOffset.UtcNow, "private investigation reason").IsSuccess);
            await services.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        }
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload))
            .Where(fact => fact.SubjectType == "refresh-token").OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "issued", "revoked" }, facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2 }, facts.Select(fact => fact.Version));
        Assert.Single(facts.Select(fact => fact.SubjectId).Distinct());
        Assert.All(pending, entry => Assert.DoesNotContain("private investigation reason", entry.Payload, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task RefreshAndReplay_RecordTokenConsumptionAndCommittedRevocation_WithoutSecrets()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        Assert.True((await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateUserCommand("token-fact-user", "private-token-password")))).IsSuccess);
        var login = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new LoginCommand("token-fact-user", "private-token-password")));
        Assert.True(login.IsSuccess);
        var refreshed = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new RefreshTokenCommand(login.Value.Tokens.RefreshToken)));
        Assert.True(refreshed.IsSuccess);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True((await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
                .SendAsync(new RefreshTokenCommand(login.Value.Tokens.RefreshToken)))).IsFailure);
        }
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload)).ToArray();
        var users = facts.Where(fact => fact.SubjectType == "user").OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "created", "login-succeeded", "sessions-revoked" }, users.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2, 3 }, users.Select(fact => fact.Version));
        var tokens = facts.Where(fact => fact.SubjectType == "refresh-token").ToArray();
        Assert.Equal(3, tokens.Length);
        var consumed = Assert.Single(tokens, fact => fact.Operation == "consumed");
        Assert.Equal(2, consumed.Version);
        Assert.Equal(2, tokens.Count(fact => fact.Operation == "issued"));
        Assert.Equal(2, tokens.Select(fact => fact.SubjectId).Distinct().Count());
        Assert.Single(tokens, fact => fact.SubjectId == consumed.SubjectId && fact.Version == 1 && fact.Operation == "issued");
        foreach (var entry in pending)
        {
            Assert.False(entry.Payload.Contains(login.Value.Tokens.AccessToken, StringComparison.Ordinal));
            Assert.False(entry.Payload.Contains(login.Value.Tokens.RefreshToken, StringComparison.Ordinal));
            Assert.False(entry.Payload.Contains(refreshed.Value.RefreshToken, StringComparison.Ordinal));
            Assert.DoesNotContain("hash", entry.Payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [PostgresFact]
    public async Task AssigningRole_RecordsTheUserChange_OnceDespiteRepeatedCommand()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var user = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateUserCommand("permission-fact-user", "private-permission-password")));
        var role = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateRoleCommand("permission-fact-role", "Private role")));
        Assert.True(user.IsSuccess);
        Assert.True(role.IsSuccess);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True((await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
                .SendAsync(new AssignRoleCommand(user.Value, role.Value)))).IsSuccess);
        }
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload))
            .Where(fact => fact.SubjectType == "user").OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "created", "role-assigned" }, facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2 }, facts.Select(fact => fact.Version));
        Assert.All(facts, fact => Assert.Equal(user.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.SubjectId));
        Assert.Equal(new IdentitySubjectReference("role", role.Value), facts[1].RelatedSubject);
    }

    [PostgresFact]
    public async Task RolePermissionChanges_KeepRootIdentityAndVersion_AndIgnoreRepeatedGrants()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var menu = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateMenuCommand("Audit role menu", 1, null)));
        Assert.True(menu.IsSuccess);
        var role = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
            .SendAsync(new CreateRoleCommand("audit-role", "Private role title")));
        Assert.True(role.IsSuccess);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var granted = await IdentityTestHost.InScopeAsync(provider, services => services.GetRequiredService<ISender>()
                .SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId)));
            Assert.True(granted.IsSuccess);
        }
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload))
            .Where(fact => fact.SubjectType == "role").OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "created", "menu-granted" }, facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2 }, facts.Select(fact => fact.Version));
        Assert.All(facts, fact => Assert.Equal(role.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.SubjectId));
        Assert.Equal(new IdentitySubjectReference("menu", menu.Value.MenuId), facts[1].RelatedSubject);
        Assert.All(pending, entry => Assert.DoesNotContain("Private role title", entry.Payload, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task RetryingFailedSave_InTheSameStorageScope_DoesNotDuplicateTheCommittedFact()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var hash = services.GetRequiredService<IPasswordHasher>().Hash("private-retry-password");
        var user = User.Register(new UserId(64901), UserName.Create("retry-fact-user").Value,
            PasswordHash.Create(hash).Value, DateTimeOffset.UtcNow);
        await services.GetRequiredService<IUserRepository>().AddAsync(user);
        var unit = services.GetRequiredService<IIdentityUnitOfWork>();
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION identity.reject_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected source fact failure'; END $$;
            CREATE TRIGGER reject_fact BEFORE INSERT ON identity.outbox
            FOR EACH ROW EXECUTE FUNCTION identity.reject_fact();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => unit.SaveChangesAsync());
            var outbox = services.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey);
            Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow));
            await using (var recover = new NpgsqlCommand("DROP TRIGGER reject_fact ON identity.outbox", connection))
            { await recover.ExecuteNonQueryAsync(); }
            await unit.SaveChangesAsync();
            Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow));
            // 成功保存后的空保存也不能再次产生事实。
            await unit.SaveChangesAsync();
            Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("""
                DROP TRIGGER IF EXISTS reject_fact ON identity.outbox;
                DROP FUNCTION IF EXISTS identity.reject_fact();
                """, connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
