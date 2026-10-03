using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.Identity.IntegrationTests;

[Collection(IdentityDatabaseGroup.Name)]
public sealed class IdentityFactCapacityTests(IdentityDatabaseFixture fixture)
{
    [PostgresFact]
    public async Task WrongPassword_WhenAuditCapacityIsFull_DoesNotPretendItsFailureCountWasSaved()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var user = await sender.SendAsync(new CreateUserCommand("capacity-login", "capacity-test-password"));
        Assert.True(user.IsSuccess);
        await SetRemainingAsync(0);
        var failedStorage = await sender.SendAsync(new LoginCommand("capacity-login", "wrong-password"));
        Assert.Equal("identity.audit_capacity.exhausted", failedStorage.Error.Code);
        await using (var read = provider.CreateAsyncScope())
        {
            var unchanged = await read.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(user.Value));
            Assert.NotNull(unchanged);
            Assert.Equal(0, unchanged.FailedLoginCount);
            Assert.Equal(1, unchanged.Version);
        }
        await SetRemainingAsync(1);
        var persistedRejection = await sender.SendAsync(new LoginCommand("capacity-login", "wrong-password"));
        Assert.Equal("identity.credentials.invalid", persistedRejection.Error.Code);
        await using var committed = provider.CreateAsyncScope();
        var changed = await committed.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(user.Value));
        Assert.NotNull(changed);
        Assert.Equal(1, changed.FailedLoginCount);
        Assert.Equal(2, changed.Version);
        Assert.Equal(2, (await committed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity").ReadPendingAsync(20, DateTimeOffset.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task PermissionChange_RollsBackOnCapacityFailure_AndSameScopeRetryInvalidatesOnlyAfterCommit()
    {
        await fixture.ResetAsync();
        var cache = new RecordingPermissionCache();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString, services => services.AddSingleton<IPermissionCache>(cache));
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var user = await sender.SendAsync(new CreateUserCommand("capacity-user", "capacity-test-password"));
        var role = await sender.SendAsync(new CreateRoleCommand("capacity-role", "Capacity role"));
        var menu = await sender.SendAsync(new CreateMenuCommand("Capacity menu", 0, null));
        Assert.True(user.IsSuccess);
        Assert.True(role.IsSuccess);
        Assert.True(menu.IsSuccess);
        var originalInvalidations = cache.Invalidations;
        await SetRemainingAsync(0);
        var refused = await sender.SendAsync(new AssignRoleCommand(user.Value, role.Value));
        Assert.Equal("identity.audit_capacity.exhausted", refused.Error.Code);
        Assert.Equal(originalInvalidations, cache.Invalidations);
        await using (var read = provider.CreateAsyncScope())
        {
            var unchanged = await read.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(user.Value));
            Assert.NotNull(unchanged);
            Assert.Empty(unchanged.RoleIds);
            Assert.Equal(1, unchanged.Version);
        }
        await SetRemainingAsync(1);
        Assert.True((await sender.SendAsync(new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
        Assert.Equal(originalInvalidations + 1, cache.Invalidations);
        Assert.True((await sender.SendAsync(new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
        Assert.Equal(originalInvalidations + 1, cache.Invalidations);
        await using var committed = provider.CreateAsyncScope();
        var changed = await committed.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(user.Value));
        Assert.NotNull(changed);
        Assert.Equal(role.Value, Assert.Single(changed.RoleIds).Value);
        Assert.Equal(2, changed.Version);
        var entries = await committed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity").ReadPendingAsync(20, DateTimeOffset.UtcNow);
        Assert.Equal(5, entries.Count);

        var refusedGrant = await sender.SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId));
        Assert.Equal("identity.audit_capacity.exhausted", refusedGrant.Error.Code);
        Assert.Equal(originalInvalidations + 1, cache.Invalidations);
        await using (var read = provider.CreateAsyncScope())
        {
            var unchangedRole = Assert.Single(await read.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(role.Value)]));
            Assert.Empty(unchangedRole.GrantedMenuIds);
            Assert.Equal(1, unchangedRole.Version);
        }
        await SetRemainingAsync(1);
        Assert.True((await sender.SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).IsSuccess);
        Assert.Equal(originalInvalidations + 2, cache.Invalidations);
        Assert.True((await sender.SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).IsSuccess);
        Assert.Equal(originalInvalidations + 2, cache.Invalidations);
    }

    private async Task SetRemainingAsync(int remaining)
    {
        await using var connection = await fixture.Database.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE identity.fact_capacity SET \"MaxRecords\" = \"RetainedRecords\" + @remaining", connection);
        command.Parameters.AddWithValue("remaining", remaining);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class RecordingPermissionCache : IPermissionCache
    {
        public int Invalidations { get; private set; }
        public void Invalidate() => Invalidations++;
        public Task<Result<PermissionKeySet>> GetAsync(UserId userId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This command does not read cached permissions.");
    }
}
