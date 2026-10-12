using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class RoleLogicalDeleteMigrationTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task RoleLogicalDeleteMigration_RejectsRollbackWhenDeletedHistoryWouldBeRestored()
    {
        await using var database = await databases.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        long roleId;
        await using (var app = new PersistentIdentityApp(database.ConnectionString, "role-delete-migration-root", schedulingWorkerEnabled: false))
        {
            using var root = app.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "role-delete-migration-root");
            using var created = await root.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
                new { code = "migration-deleted-role", name = "Migration deleted role" }, deadline.Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            roleId = (await created.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/identity/roles/{roleId}", UriKind.Relative))
            { Content = JsonContent.Create(new { expectedVersion = 1 }) };
            using var deleted = await root.SendAsync(request, deadline.Token);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await using var context = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, IdentityDbContext.SchemaName).Options);
        var migrator = context.GetService<IMigrator>();
        var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
            JourneyDatabaseOperation.RunAsync(
                () => migrator.MigrateAsync("20261012024820_RoleEnabled", deadline.Token), deadline.Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
        Assert.Equal("identity_role_deleted_history_exists", refusal.ConstraintName);
        Assert.Contains("20261012052639_RoleLogicalDelete", await context.Database.GetAppliedMigrationsAsync(deadline.Token));
    }
}
