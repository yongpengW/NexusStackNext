using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.Scheduling.Infrastructure.Persistence;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactCapacityPolicyMigrationTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task IdentityPolicyMigration_PreservesOldRolePolicyAndUsage_AndRejectsRollbackOfAcceptedHistory()
    {
        await using var database = await databases.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Role originalRole;
        CommittedFactCapacitySnapshot originalCapacity;
        IReadOnlyList<OutboxEntry> originalFacts;
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization;
        await using (var original = new PersistentIdentityApp(database.ConnectionString, PlatformAppWithRootAccount.RootPassword,
            schedulingWorkerEnabled: false))
        {
            using var client = original.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", PlatformAppWithRootAccount.RootPassword);
            authorization = client.DefaultRequestHeaders.Authorization;
            using var created = await client.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
                new { code = "pre-policy.role", name = "Pre-policy role" }, deadline.Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var roleId = (await created.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();
            await using var scope = original.Services.CreateAsyncScope();
            originalRole = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRoleRepository>()
                .FindManyAsync([new(roleId)], deadline.Token)).Snapshot();
            Assert.Equal(1, originalRole.Version);
            originalCapacity = (await scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("identity")
                .ReadAsync(deadline.Token)).Value;
            originalFacts = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity")
                .ReadPendingAsync(100, DateTimeOffset.MaxValue, deadline.Token);
            Assert.NotEmpty(originalFacts);
            Assert.Equal(originalFacts.Count, originalCapacity.RetainedRecords);
        }
        await using var context = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, IdentityDbContext.SchemaName).Options);
        var migrator = context.GetService<IMigrator>();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004065429_FactCapacityWaitBudget", deadline.Token), deadline.Token);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var arrange = new NpgsqlCommand("""
                UPDATE identity.fact_capacity SET "MaxRecords" = 16, "MaxPayloadBytes" = 32768, "MaxRecordPayloadBytes" = 4096
                """, connection);
            Assert.Equal(1, await arrange.ExecuteNonQueryAsync(deadline.Token));
        }
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        Assert.False(context.Database.HasPendingModelChanges());
        await using var upgraded = new PersistentIdentityApp(database.ConnectionString, PlatformAppWithRootAccount.RootPassword,
            schedulingWorkerEnabled: false);
        using var restored = upgraded.CreateClient();
        restored.DefaultRequestHeaders.Authorization = authorization;
        var policyPath = new Uri("/api/identity/audit-capacity", UriKind.Relative);
        using var initialResponse = await restored.GetAsync(policyPath, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadApiDataAsync();
        Assert.Equal(1, initial.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(16, initial.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(32768, initial.GetProperty("maxPayloadBytes").ReadHttpInt64());
        Assert.Equal(4096, initial.GetProperty("maxRecordPayloadBytes").GetInt32());
        Assert.Equal(originalCapacity.RetainedRecords, initial.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.RetainedPayloadBytes, initial.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(0, initial.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000014"), 1, 17, 32768, 4096, "operator-adjustment");
        using var accepted = await restored.PutAsJsonAsync(policyPath, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        using var replay = await restored.PutAsJsonAsync(policyPath, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004065429_FactCapacityWaitBudget", deadline.Token), deadline.Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
        Assert.Equal("identity_fact_policy_history_exists", refusal.ConstraintName);
        Assert.Contains("20261004152243_AuditedFactCapacityPolicy", await context.Database.GetAppliedMigrationsAsync(deadline.Token));
        await using var observer = upgraded.Services.CreateAsyncScope();
        var unchanged = Assert.Single(await observer.ServiceProvider.GetRequiredService<IRoleRepository>()
            .FindManyAsync([originalRole.Id], deadline.Token));
        Assert.Equal(originalRole.Version, unchanged.Version);
        Assert.Equal(originalRole.Code, unchanged.Code);
        Assert.Equal(originalRole.Name, unchanged.Name);
        Assert.Equal(originalRole.CreatedAt, unchanged.CreatedAt);
        Assert.Equal(originalRole.CreatedBy, unchanged.CreatedBy);
        Assert.Equal(originalRole.UpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(originalRole.UpdatedBy, unchanged.UpdatedBy);
        Assert.Equal(originalRole.GrantedMenuIds, unchanged.GrantedMenuIds);
        var policies = observer.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("identity");
        var after = (await policies.ReadPolicyAsync(deadline.Token)).Value;
        Assert.Equal(2, after.PolicyRevision);
        Assert.Equal(17, after.MaxRecords);
        Assert.Equal(originalCapacity.RetainedRecords, after.RetainedRecords);
        Assert.Equal(originalCapacity.RetainedPayloadBytes, after.RetainedPayloadBytes);
        Assert.Equal(1, after.ControlCapacity.RetainedRecords);
        var preserved = await observer.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity")
            .ReadPendingAsync(100, DateTimeOffset.MaxValue, deadline.Token);
        Assert.Equal(originalFacts.Count + 1, preserved.Count);
        Assert.All(originalFacts, fact => Assert.Contains(fact, preserved));
        Assert.Equal(receipt.GetProperty("eventId").GetGuid(), Assert.Single(preserved, fact => fact.EventName == "identity.fact-capacity-policy-changed.v1").Id);
    }

    [PostgresFact]
    public async Task PlatformPolicyMigration_PreservesOldSettingPolicyAndUsage_AndRejectsRollbackOfAcceptedHistory()
    {
        await using var database = await databases.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var settingPath = new Uri("/api/platform/settings/pre-policy.sender", UriKind.Relative);
        string originalSetting;
        CommittedFactCapacitySnapshot originalCapacity;
        IReadOnlyList<OutboxEntry> originalFacts;
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization;
        await using (var original = new PersistentIdentityApp(database.ConnectionString, PlatformAppWithRootAccount.RootPassword,
            schedulingWorkerEnabled: false))
        {
            using var client = original.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", PlatformAppWithRootAccount.RootPassword);
            authorization = client.DefaultRequestHeaders.Authorization;
            using var created = await client.PutAsJsonAsync(settingPath,
                new { value = "pre-policy-value", description = "pre-policy-description", expectedVersion = 0 }, deadline.Token);
            Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
            using var read = await client.GetAsync(settingPath, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            originalSetting = (await read.Content.ReadApiDataAsync()).GetRawText();
            await using var scope = original.Services.CreateAsyncScope();
            originalCapacity = (await scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform")
                .ReadAsync(deadline.Token)).Value;
            originalFacts = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform")
                .ReadPendingAsync(10, DateTimeOffset.MaxValue, deadline.Token);
            Assert.Single(originalFacts);
            Assert.Equal(1, originalCapacity.RetainedRecords);
        }
        await using var context = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, PlatformDbContext.SchemaName).Options);
        var migrator = context.GetService<IMigrator>();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004064942_FactCapacityWaitBudget", deadline.Token), deadline.Token);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var arrange = new NpgsqlCommand("""
                UPDATE platform.fact_capacity SET "MaxRecords" = 3, "MaxPayloadBytes" = 8192, "MaxRecordPayloadBytes" = 2048
                """, connection);
            Assert.Equal(1, await arrange.ExecuteNonQueryAsync(deadline.Token));
        }
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        Assert.False(context.Database.HasPendingModelChanges());
        await using var upgraded = new PersistentIdentityApp(database.ConnectionString, PlatformAppWithRootAccount.RootPassword,
            schedulingWorkerEnabled: false);
        using var restored = upgraded.CreateClient();
        restored.DefaultRequestHeaders.Authorization = authorization;
        var policyPath = new Uri("/api/platform/audit-capacity", UriKind.Relative);
        using var initialResponse = await restored.GetAsync(policyPath, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadApiDataAsync();
        Assert.Equal(1, initial.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(3, initial.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(8192, initial.GetProperty("maxPayloadBytes").ReadHttpInt64());
        Assert.Equal(2048, initial.GetProperty("maxRecordPayloadBytes").GetInt32());
        Assert.Equal(1, initial.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.RetainedPayloadBytes, initial.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(0, initial.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000014"), 1, 4, 8192, 2048, "operator-adjustment");
        using var accepted = await restored.PutAsJsonAsync(policyPath, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        using var replay = await restored.PutAsJsonAsync(policyPath, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004064942_FactCapacityWaitBudget", deadline.Token), deadline.Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
        Assert.Equal("platform_fact_policy_history_exists", refusal.ConstraintName);
        Assert.Contains("20261004141238_AuditedFactCapacityPolicy", await context.Database.GetAppliedMigrationsAsync(deadline.Token));
        using var unchanged = await restored.GetAsync(settingPath, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        using var savedSetting = JsonDocument.Parse(originalSetting);
        var restoredSetting = await unchanged.Content.ReadApiDataAsync();
        // `at` is the query time; only persisted setting fields and audit metadata must remain unchanged.
        foreach (var property in new[] { "key", "scope", "value", "version", "description", "audit" })
        {
            Assert.Equal(savedSetting.RootElement.GetProperty(property).GetRawText(), restoredSetting.GetProperty(property).GetRawText());
        }
        using var afterResponse = await restored.GetAsync(policyPath, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
        var after = await afterResponse.Content.ReadApiDataAsync();
        Assert.Equal(2, after.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(4, after.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.RetainedPayloadBytes, after.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(1, after.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        await using var observer = upgraded.Services.CreateAsyncScope();
        var preserved = await observer.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform")
            .ReadPendingAsync(10, DateTimeOffset.MaxValue, deadline.Token);
        Assert.Equal(2, preserved.Count);
        Assert.All(originalFacts, fact => Assert.Contains(fact, preserved));
        Assert.Equal(receipt.GetProperty("eventId").GetGuid(), Assert.Single(preserved, fact => fact.EventName == "platform.fact-capacity-policy-changed.v1").Id);
    }

    [PostgresFact]
    public async Task SchedulingPolicyMigration_PreservesOldPlanPolicyAndUsage_AndRejectsRollbackOfAcceptedHistory()
    {
        await using var database = await databases.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string originalPlan;
        CommittedFactCapacitySnapshot originalCapacity;
        IReadOnlyList<OutboxEntry> originalFacts;
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization;
        await using (var original = new PersistentIdentityApp(database.ConnectionString, "scheduling-upgrade-root", schedulingWorkerEnabled: false))
        {
            using var client = original.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "scheduling-upgrade-root");
            authorization = client.DefaultRequestHeaders.Authorization;
            using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
                new
                {
                    code = "pre-policy-plan",
                    intervalSeconds = 30,
                    firstRunInSeconds = 3600,
                    targetKind = "costing.recalculate",
                    targetId = Guid.Parse("11111111-2222-3333-4444-555555555555")
                }, deadline.Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var listed = await client.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), deadline.Token);
            originalPlan = (await listed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(deadline.Token)).GetProperty("data").GetRawText();
            await using var originalScope = original.Services.CreateAsyncScope();
            originalFacts = await originalScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling")
                .ReadPendingAsync(10, DateTimeOffset.MaxValue, deadline.Token);
            Assert.Single(originalFacts);
            originalCapacity = (await originalScope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling")
                .ReadAsync(deadline.Token)).Value;
        }
        await using var context = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, SchedulingDbContext.SchemaName).Options);
        var migrator = context.GetService<IMigrator>();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004065840_FactCapacityWaitBudget", deadline.Token), deadline.Token);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var arrange = new NpgsqlCommand("""
                UPDATE scheduling.fact_capacity SET "MaxRecords" = 1, "MaxPayloadBytes" = 8192, "MaxRecordPayloadBytes" = 2048
                """, connection);
            Assert.Equal(1, await arrange.ExecuteNonQueryAsync(deadline.Token));
        }
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        Assert.False(context.Database.HasPendingModelChanges());
        await using var upgraded = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var restored = upgraded.CreateClient();
        restored.DefaultRequestHeaders.Authorization = authorization;
        var path = new Uri("/api/scheduling/audit-capacity", UriKind.Relative);
        using var initialResponse = await restored.GetAsync(path, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadApiDataAsync();
        Assert.Equal(1, initial.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(1, initial.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(8192, initial.GetProperty("maxPayloadBytes").ReadHttpInt64());
        Assert.Equal(2048, initial.GetProperty("maxRecordPayloadBytes").GetInt32());
        Assert.Equal(1, initial.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.RetainedPayloadBytes, initial.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(0, initial.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2, 268435456, 16384, "operator-adjustment");
        using var accepted = await restored.PutAsJsonAsync(path, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        using var replay = await restored.PutAsJsonAsync(path, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004065840_FactCapacityWaitBudget", deadline.Token), deadline.Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
        Assert.Equal("scheduling_fact_policy_history_exists", refusal.ConstraintName);
        Assert.Contains("20261004162527_AuditedFactCapacityPolicy", await context.Database.GetAppliedMigrationsAsync(deadline.Token));
        using var retainedResponse = await restored.GetAsync(path, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, retainedResponse.StatusCode);
        var retained = await retainedResponse.Content.ReadApiDataAsync();
        Assert.Equal(2, retained.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(1, retained.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.RetainedPayloadBytes, retained.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(1, retained.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        using var unchangedResponse = await restored.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), deadline.Token);
        Assert.Equal(originalPlan, (await unchangedResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(deadline.Token)).GetProperty("data").GetRawText());
        await using var scope = upgraded.Services.CreateAsyncScope();
        var preserved = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling").ReadPendingAsync(10, DateTimeOffset.MaxValue, deadline.Token);
        Assert.All(originalFacts, fact => Assert.Contains(fact, preserved));
    }

    [PostgresFact]
    public async Task FilesPolicyMigration_PreservesOldBusinessPolicyAndUsage_AndRejectsRollbackOfAcceptedHistory()
    {
        await using var database = await databases.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var context = new FilesDbContext(new DbContextOptionsBuilder<FilesDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, FilesDbContext.SchemaName).Options);
        var migrator = context.GetService<IMigrator>();
        await using var storage = FilesCommittedAuditTests.BuildStorage(database.ConnectionString);
        await using var scope = storage.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var file = StoredFile.Register(new StoredFileId(99873), FileName.Create("pre-policy.bin").Value,
            "application/octet-stream", "owner", DateTimeOffset.UtcNow).Value;
        Assert.True(file.MarkStored("pre-policy-storage", 3).IsSuccess);
        await files.SaveAsync(file, cancellationToken: deadline.Token);
        var committed = await files.FindAsync(file.Id, deadline.Token);
        Assert.NotNull(committed);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var originalFacts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue, deadline.Token);
        Assert.Equal(2, originalFacts.Count);
        var capacityReader = new PostgresCommittedFactCapacityReader<FilesDbContext>(context, "files", new());
        var originalCapacity = (await capacityReader.ReadAsync(deadline.Token)).Value;
        // Arrange through the current public repository before removing newer columns. The rollback
        // preserves this ordinary file and its facts as real pre-policy state; no control history exists.
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004065631_FactCapacityWaitBudget", deadline.Token), deadline.Token);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var arrange = new NpgsqlCommand("""
                UPDATE files.fact_capacity SET "MaxRecords" = 2, "MaxPayloadBytes" = 8192, "MaxRecordPayloadBytes" = 2048
                """, connection);
            Assert.Equal(1, await arrange.ExecuteNonQueryAsync(deadline.Token));
        }
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        Assert.False(context.Database.HasPendingModelChanges());
        await using var app = new PersistentIdentityApp(database.ConnectionString, "files-upgrade-root", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-upgrade-root");
        var path = new Uri("/api/files/audit-capacity", UriKind.Relative);
        using var initialResponse = await client.GetAsync(path, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadApiDataAsync();
        Assert.Equal(1, initial.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(2, initial.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(8192, initial.GetProperty("maxPayloadBytes").ReadHttpInt64());
        Assert.Equal(2048, initial.GetProperty("maxRecordPayloadBytes").GetInt32());
        Assert.Equal(2, initial.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.RetainedPayloadBytes, initial.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(0, initial.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 4, 268435456, 16384, "operator-adjustment");
        using var accepted = await client.PutAsJsonAsync(path, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        using var replay = await client.PutAsJsonAsync(path, request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
            JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004065631_FactCapacityWaitBudget", deadline.Token), deadline.Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
        Assert.Equal("files_fact_policy_history_exists", refusal.ConstraintName);
        Assert.Contains("20261004155433_AuditedFactCapacityPolicy", await context.Database.GetAppliedMigrationsAsync(deadline.Token));
        // EF can finish newer Down migrations before the protected policy migration refuses.
        // Restore those optional columns before using the current model; policy history must survive both paths.
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: deadline.Token), deadline.Token);
        Assert.False(context.Database.HasPendingModelChanges());
        using var after = await client.GetAsync(path, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var retained = await after.Content.ReadApiDataAsync();
        Assert.Equal(2, retained.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(4, retained.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(2, retained.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(originalCapacity.RetainedPayloadBytes, retained.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(1, retained.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        var unchanged = await files.FindAsync(file.Id, deadline.Token);
        Assert.NotNull(unchanged);
        Assert.Equal(committed.Version, unchanged.Version);
        Assert.Equal(committed.CreatedAt, unchanged.CreatedAt);
        Assert.Equal(committed.UpdatedAt, unchanged.UpdatedAt);
        var preservedFacts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue, deadline.Token);
        Assert.All(originalFacts, fact => Assert.Contains(fact, preservedFacts));
    }
}
