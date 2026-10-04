using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryIdentityFactCapacityTests
{
    [Fact]
    public async Task PermissionRefusal_PreservesTheCache_AndSameScopeRetryInvalidatesOnlyAfterCommit()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var cache = new RecordingPermissionCache();
        await using var baseApp = new CapacityApp(clock, maxRecords: 4) { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IPermissionCache>(cache)));
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var user = await sender.SendAsync(new CreateUserCommand("memory-capacity-permission", "capacity-test-password"));
        var role = await sender.SendAsync(new CreateRoleCommand("memory-capacity-permission", "Permission"));
        var menu = await sender.SendAsync(new CreateMenuCommand("Permission", 0, null));
        Assert.True(user.IsSuccess);
        Assert.True(role.IsSuccess);
        Assert.True(menu.IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("identity");
        var seeds = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(4, seeds.Count);
        var invalidations = cache.Invalidations;
        Assert.Equal(IdentityAuditCapacityException.Exhausted, (await sender.SendAsync(new AssignRoleCommand(user.Value, role.Value))).Error);
        Assert.Equal(IdentityAuditCapacityException.Exhausted, (await sender.SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).Error);
        Assert.Equal(invalidations, cache.Invalidations);
        await using (var observer = app.Services.CreateAsyncScope())
        {
            var unchanged = await observer.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(user.Value));
            Assert.NotNull(unchanged);
            Assert.Empty(unchanged.RoleIds);
            Assert.Equal(1, unchanged.Version);
            Assert.Null(unchanged.UpdatedAt);
            var unchangedRole = Assert.Single(await observer.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(role.Value)]));
            Assert.Empty(unchangedRole.GrantedMenuIds);
            Assert.Equal(1, unchangedRole.Version);
            Assert.Null(unchangedRole.UpdatedAt);
        }
        Assert.Equal(seeds, await outbox.ReadPendingAsync(10, clock.UtcNow));
        foreach (var entry in seeds.Take(2)) { await outbox.MarkDeliveredAsync(entry.Id, clock.UtcNow); }
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(2, await cleanup.CleanupAsync());
        Assert.True((await sender.SendAsync(new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
        Assert.Equal(invalidations + 1, cache.Invalidations);
        Assert.True((await sender.SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).IsSuccess);
        Assert.Equal(invalidations + 2, cache.Invalidations);
        Assert.True((await sender.SendAsync(new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
        Assert.True((await sender.SendAsync(new GrantMenuToRoleCommand(role.Value, menu.Value.MenuId))).IsSuccess);
        Assert.Equal(invalidations + 2, cache.Invalidations);
        Assert.Equal(4, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
        await using var reader = app.Services.CreateAsyncScope();
        var committed = await reader.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(user.Value));
        Assert.NotNull(committed);
        Assert.Equal(role.Value, Assert.Single(committed.RoleIds).Value);
        Assert.Equal(2, committed.Version);
        var committedRole = Assert.Single(await reader.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(role.Value)]));
        Assert.Equal(menu.Value.MenuId, Assert.Single(committedRole.GrantedMenuIds).Value);
        Assert.Equal(2, committedRole.Version);
    }

    [Fact]
    public async Task Utf8Limits_RejectSingleAndWholeBatchOverflow_AndRetryUsesOnlyCommittedBytes()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var serializer = new KnownPayloadSerializer { Payload = "中文" };
        await using var baseApp = new CapacityApp(clock, maxRecords: 10, maxPayloadBytes: 7, maxRecordPayloadBytes: 3)
        { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IIntegrationEventSerializer>(serializer)));
        await using var scope = app.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var unit = scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var first = Role.Create(new RoleId(99831), RoleCode.Create("utf8-first").Value, RoleName.Create("First").Value);
        await roles.AddAsync(first);
        await Assert.ThrowsAsync<IdentityAuditCapacityException>(() => unit.SaveChangesAsync());
        Assert.Empty(await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.Equal(default, first.CreatedAt);

        serializer.Payload = "中"; // A known three-byte UTF-8 payload.
        Assert.Equal(1, await unit.SaveChangesAsync());
        var batch = Role.Create(new RoleId(99832), RoleCode.Create("utf8-batch").Value, RoleName.Create("Batch").Value);
        Assert.True(batch.Grant(new MenuId(99833), clock.UtcNow).IsSuccess);
        await roles.AddAsync(batch);
        serializer.Payload = "文";
        await Assert.ThrowsAsync<IdentityAuditCapacityException>(() => unit.SaveChangesAsync());
        Assert.Equal("中", Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)).Payload);
        await using (var observer = app.Services.CreateAsyncScope())
        {
            Assert.Null(await observer.ServiceProvider.GetRequiredService<IRoleRepository>().FindByCodeAsync(batch.Code));
        }
        Assert.Equal(default, batch.CreatedAt);
        serializer.Payload = "a";
        Assert.Equal(1, await unit.SaveChangesAsync()); // Three retained bytes plus two one-byte facts.
        Assert.Equal(0, await unit.SaveChangesAsync());
        var last = Role.Create(new RoleId(99834), RoleCode.Create("utf8-last").Value, RoleName.Create("Last").Value);
        await roles.AddAsync(last);
        serializer.Payload = "文";
        await Assert.ThrowsAsync<IdentityAuditCapacityException>(() => unit.SaveChangesAsync());
        serializer.Payload = "ab";
        Assert.Equal(1, await unit.SaveChangesAsync()); // Exactly seven committed bytes.
        Assert.Equal(new[] { "a", "a", "ab", "中" }, (await outbox.ReadPendingAsync(10, clock.UtcNow))
            .Select(entry => entry.Payload).Order(StringComparer.Ordinal));
        await roles.AddAsync(Role.Create(new RoleId(99835), RoleCode.Create("utf8-excess").Value, RoleName.Create("Excess").Value));
        serializer.Payload = "a";
        await Assert.ThrowsAsync<IdentityAuditCapacityException>(() => unit.SaveChangesAsync());
    }

    [Fact]
    public async Task WrongPasswordAtCapacity_DoesNotSaveItsRejectedState_AndSameCommandScopeCanRecover()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var app = new CapacityApp(clock, maxRecords: 1) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var user = await sender.SendAsync(new CreateUserCommand("memory-capacity-login", "capacity-test-password"));
        Assert.True(user.IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("identity");
        var created = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        var refused = await sender.SendAsync(new LoginCommand("memory-capacity-login", "wrong-password"));
        Assert.Equal(IdentityAuditCapacityException.Exhausted, refused.Error);
        await using (var observer = app.Services.CreateAsyncScope())
        {
            var unchanged = await observer.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(user.Value));
            Assert.NotNull(unchanged);
            Assert.Equal(0, unchanged.FailedLoginCount);
            Assert.Equal(1, unchanged.Version);
        }
        Assert.Equal(created, Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)));
        await outbox.MarkDeliveredAsync(created.Id, clock.UtcNow);
        Assert.Equal(0, await cleanup.CleanupAsync());
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(1, await cleanup.CleanupAsync());

        var recovered = await sender.SendAsync(new LoginCommand("memory-capacity-login", "wrong-password"));
        Assert.Equal("identity.credentials.invalid", recovered.Error.Code);
        await using var reader = app.Services.CreateAsyncScope();
        var committed = await reader.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(user.Value));
        Assert.NotNull(committed);
        Assert.Equal(1, committed.FailedLoginCount);
        Assert.Equal(2, committed.Version);
        var fact = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.Equal("login-failed", new SystemTextJsonIntegrationEventSerializer().Deserialize<IdentityEntityCommittedV1>(fact.Payload).Operation);
    }

    [Fact]
    public async Task WholeRoleFactBatch_RejectsWithoutPublishing_AndStandaloneSaveRetainsItsWorkForRecovery()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var app = new CapacityApp(clock) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var unit = scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("identity");
        for (var index = 0; index < 2; index++)
        {
            await roles.AddAsync(Role.Create(new RoleId(99810 + index), RoleCode.Create($"capacity-seed-{index}").Value, RoleName.Create("Seed role").Value));
            Assert.Equal(1, await unit.SaveChangesAsync());
        }
        var seeds = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(2, seeds.Count);
        var code = RoleCode.Create("capacity-batch").Value;
        var role = Role.Create(new RoleId(99820), code, RoleName.Create("Batch role").Value);
        Assert.True(role.Grant(new MenuId(99821), clock.UtcNow).IsSuccess);
        await roles.AddAsync(role);
        await Assert.ThrowsAsync<IdentityAuditCapacityException>(() => unit.SaveChangesAsync());
        Assert.Equal(seeds, await outbox.ReadPendingAsync(10, clock.UtcNow));
        await using (var observer = app.Services.CreateAsyncScope())
        {
            Assert.Null(await observer.ServiceProvider.GetRequiredService<IRoleRepository>().FindByCodeAsync(code));
        }
        Assert.Equal(default, role.CreatedAt);

        foreach (var seed in seeds) { await outbox.MarkDeliveredAsync(seed.Id, clock.UtcNow); }
        Assert.Equal(0, await cleanup.CleanupAsync());
        await Assert.ThrowsAsync<IdentityAuditCapacityException>(() => unit.SaveChangesAsync());
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(2, await cleanup.CleanupAsync());
        Assert.Equal(1, await unit.SaveChangesAsync());
        Assert.Equal(0, await unit.SaveChangesAsync());
        var facts = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(2, facts.Count);
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        Assert.Equal(new[] { "created", "menu-granted" }, facts.Select(entry => serializer.Deserialize<IdentityEntityCommittedV1>(entry.Payload).Operation).Order(StringComparer.Ordinal));
        Assert.NotEqual(default, role.CreatedAt);
        await using var reader = app.Services.CreateAsyncScope();
        var committed = await reader.ServiceProvider.GetRequiredService<IRoleRepository>().FindByCodeAsync(code);
        Assert.NotNull(committed);
        Assert.Contains(new MenuId(99821), committed.GrantedMenuIds);
    }

    private sealed class CapacityApp(MutableClock clock, long maxRecords = 3,
        long maxPayloadBytes = 268435456, int maxRecordPayloadBytes = 16384) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:AuditDelivery:MemoryCapacity:MaxRecords"] = maxRecords.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Identity:AuditDelivery:MemoryCapacity:MaxPayloadBytes"] = maxPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Identity:AuditDelivery:MemoryCapacity:MaxRecordPayloadBytes"] = maxRecordPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Identity:AuditDelivery:Cleanup:Enabled"] = "false",
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock));
        }
    }
}
