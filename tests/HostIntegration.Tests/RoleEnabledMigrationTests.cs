using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class RoleEnabledMigrationTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task RoleEnabledMigration_RejectsRollbackWhenDisabledRoleWouldLoseAuthorityState()
    {
        await using var database = await databases.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        long roleId;
        await using (var app = new PersistentIdentityApp(database.ConnectionString, "role-migration-root", schedulingWorkerEnabled: false))
        {
            using var root = app.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "role-migration-root");
            using var created = await root.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
                new { code = "migration-disabled-role", name = "Migration disabled role" }, deadline.Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            roleId = (await created.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();
            using var disabled = await root.PostAsJsonAsync(
                new Uri($"/api/identity/roles/{roleId}/disable", UriKind.Relative),
                new { expectedVersion = 1 }, deadline.Token);
            Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        }

        await using var context = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, IdentityDbContext.SchemaName).Options);
        var migrator = context.GetService<IMigrator>();
        var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
            JourneyDatabaseOperation.RunAsync(
                () => migrator.MigrateAsync("20261004152243_AuditedFactCapacityPolicy", deadline.Token), deadline.Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
        Assert.Equal("identity_role_enabled_history_exists", refusal.ConstraintName);
        Assert.Contains("20261012024820_RoleEnabled", await context.Database.GetAppliedMigrationsAsync(deadline.Token));

        await using var observer = new PersistentIdentityApp(database.ConnectionString, "role-migration-root", schedulingWorkerEnabled: false);
        using var client = observer.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "role-migration-root");
        using var response = await client.GetAsync(new Uri($"/api/identity/roles/{roleId}", UriKind.Relative), deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var role = await response.Content.ReadApiDataAsync();
        Assert.False(role.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(2, role.GetProperty("version").ReadHttpInt64());
    }
}
