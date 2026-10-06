using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Identity.Application;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(PlatformJourneyDefinition.Name)]
public sealed class FactCapacityAdmissionTests(PlatformJourneyTemplate databases)
{
    [PostgresFact]
    public async Task PlatformLedgerContention_RejectsWithinItsBudget_WithoutCommitting_AndRecovers()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new AdmissionApp(database.ConnectionString);
        using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(3);
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "admission-root-password");
        var setting = new Uri("/api/platform/settings/capacity.wait", UriKind.Relative);
        var capacity = new Uri("/api/platform/audit-capacity", UriKind.Relative);
        using var initial = await client.PutAsJsonAsync(setting, new { value = "before" });
        Assert.Equal(HttpStatusCode.NoContent, initial.StatusCode);
        using var before = await client.GetAsync(capacity);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var snapshot = await before.Content.ReadApiDataAsync();
        Assert.Equal(1, snapshot.GetProperty("retainedRecords").ReadHttpInt64());

        await using var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "platform");
        try
        {
            var watch = Stopwatch.StartNew();
            using var rejected = await client.PutAsJsonAsync(setting, new { value = "rejected" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("audit_capacity.busy", error.GetProperty("errorCode").GetString());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "250ms 的容量锁等待不能落入普通命令重试。");
            Assert.DoesNotContain("Npgsql", error.GetRawText(), StringComparison.Ordinal);
            using var unchanged = await client.GetAsync(setting);
            Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
            Assert.Equal("before", (await unchanged.Content.ReadApiDataAsync()).GetProperty("value").GetString());
            using var retained = await client.GetAsync(capacity);
            Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
            Assert.Equal(snapshot.GetRawText(), (await retained.Content.ReadApiDataAsync()).GetRawText());
        }
        finally { await capacityLock.ReleaseAsync(); }

        using var recovered = await client.PutAsJsonAsync(setting, new { value = "recovered" });
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
        using var changed = await client.GetAsync(setting);
        Assert.Equal("recovered", (await changed.Content.ReadApiDataAsync()).GetProperty("value").GetString());
        using var after = await client.GetAsync(capacity);
        Assert.Equal(2, (await after.Content.ReadApiDataAsync()).GetProperty("retainedRecords").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task IdentityLedgerContention_RollsBackTheCommand_AndInvalidatesPermissionsOnlyAfterRecovery()
    {
        await using var database = await databases.CreateAsync();
        var permissions = new RecordingPermissionCache();
        await using var app = new AdmissionApp(database.ConnectionString, permissions);
        using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(3);
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "admission-root-password");
        var capacity = new Uri("/api/identity/audit-capacity", UriKind.Relative);
        using var before = await client.GetAsync(capacity);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var snapshot = await before.Content.ReadApiDataAsync();
        Assert.Equal(3, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        var invalidations = permissions.Invalidations;
        var resource = new Uri("/api/identity/api-resources", UriKind.Relative);
        var request = new { path = "/api/capacity-wait", method = "GET" };
        await using var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "identity");
        try
        {
            var watch = Stopwatch.StartNew();
            using var rejected = await client.PostAsJsonAsync(resource, request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("audit_capacity.busy", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(invalidations, permissions.Invalidations);
            using var retained = await client.GetAsync(capacity);
            Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
            Assert.Equal(snapshot.GetRawText(), (await retained.Content.ReadApiDataAsync()).GetRawText());
        }
        finally { await capacityLock.ReleaseAsync(); }

        using var recovered = await client.PostAsJsonAsync(resource, request);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal(invalidations + 1, permissions.Invalidations);
        using var after = await client.GetAsync(capacity);
        Assert.Equal(4, (await after.Content.ReadApiDataAsync()).GetProperty("retainedRecords").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task FilesLedgerContention_RejectsUploadAndDeletion_WithoutPartialFactsOrLostDownload()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new AdmissionApp(database.ConnectionString);
        using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(3);
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "admission-root-password");
        var capacity = new Uri("/api/files/audit-capacity", UriKind.Relative);
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "files"))
        {
            using var bytes = new ByteArrayContent([1, 2, 3]);
            using var rejected = await client.PostAsync(new Uri("/api/files?name=waiting.bin", UriKind.Relative), bytes);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("audit_capacity.busy", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            using var retained = await client.GetAsync(capacity);
            Assert.Equal(0, (await retained.Content.ReadApiDataAsync()).GetProperty("retainedRecords").ReadHttpInt64());
            await capacityLock.ReleaseAsync();
        }
        using var retryBytes = new ByteArrayContent([1, 2, 3]);
        using var uploaded = await client.PostAsync(new Uri("/api/files?name=waiting.bin", UriKind.Relative), retryBytes);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var fileId = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        var file = new Uri($"/api/files/{fileId}", UriKind.Relative);
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "files"))
        {
            using var rejected = await client.DeleteAsync(file);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("audit_capacity.busy", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            using var download = await client.GetAsync(file);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
            using var retained = await client.GetAsync(capacity);
            Assert.Equal(2, (await retained.Content.ReadApiDataAsync()).GetProperty("retainedRecords").ReadHttpInt64());
            await capacityLock.ReleaseAsync();
        }
        using var deleted = await client.DeleteAsync(file);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var after = await client.GetAsync(capacity);
        Assert.Equal(4, (await after.Content.ReadApiDataAsync()).GetProperty("retainedRecords").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task SchedulingLedgerContention_RejectsDefinitionAndDecision_WithoutPublishingAnOccurrence()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new AdmissionApp(database.ConnectionString);
        using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(3);
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "admission-root-password");
        var request = new { code = "wait-plan", intervalSeconds = 3600, firstRunInSeconds = 3600, targetKind = "costing.recalculate", targetId = Guid.NewGuid() };
        var path = new Uri("/api/scheduling/tasks", UriKind.Relative);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "scheduling"))
        {
            using var rejected = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("audit_capacity.busy", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            Assert.Empty(await store.ListAsync());
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
            await capacityLock.ReleaseAsync();
        }
        using var created = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var plan = Assert.Single(await store.ListAsync());
        var clock = new FixedClock(plan.NextRunAt!.Value);
        var runner = new ScheduleRunner(store, clock, new CronScheduleCalendar());
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "scheduling"))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew();
            var rejected = await runner.RunOnceAsync(deadline.Token);
            Assert.Equal(new[] { plan.Id.Value }, rejected.FailedPlanIds);
            Assert.Equal(0, rejected.Triggered);
            Assert.Equal(0, rejected.Skipped);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
            Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
            var unchanged = Assert.Single(await store.ListAsync());
            Assert.Equal(plan.Version, unchanged.Version);
            Assert.Equal(plan.NextRunAt, unchanged.NextRunAt);
            Assert.Null(unchanged.RetryAt);
            Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
            await capacityLock.ReleaseAsync();
        }
        var recovered = await runner.RunOnceAsync();
        Assert.Empty(recovered.FailedPlanIds);
        Assert.Equal(1, recovered.Triggered);
        Assert.Single((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal(3, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task CallerCancellation_RemainsCancellation_AndSameScopeCanWriteAfterRollback()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new AdmissionApp(database.ConnectionString, waitTimeout: "00:00:05");
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var key = SettingKey.Create("capacity.cancel").Value;
        Assert.True((await settings.WriteAsync(key, "before")).IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var before = await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        Assert.Single(before);
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "platform"))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var pending = settings.WriteAsync(key, "cancelled", cancellationToken: cancellation.Token);
            try
            {
                using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await capacityLock.WaitForWriterAsync(observation.Token);
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.Equal("before", await settings.ReadAsync(key));
                Assert.Equal(before, await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
            }
            finally
            {
                await cancellation.CancelAsync();
                try { await pending; } catch (OperationCanceledException) { }
                await capacityLock.ReleaseAsync();
            }
        }
        Assert.True((await settings.WriteAsync(key, "recovered")).IsSuccess);
        Assert.Equal("recovered", await settings.ReadAsync(key));
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task FilesAlreadyAcceptedDeletion_WhenCapacityLockIsBusy_RemainsRecoverable()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new AdmissionApp(database.ConnectionString);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "admission-root-password");
        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var uploaded = await client.PostAsync(new Uri("/api/files?name=pending-delete.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var id = new StoredFileId((await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64());
        await using var scope = app.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var file = await files.FindAsync(id);
        Assert.NotNull(file);
        var version = file.Version;
        Assert.True(file.Delete().IsSuccess);
        await files.SaveAsync(file, version);
        var accepted = Assert.Single(await files.PendingDeletionsAsync(DateTimeOffset.UtcNow, 10));
        Assert.Equal(3, accepted.Version);
        var recovery = scope.ServiceProvider.GetRequiredService<FileRecovery>();
        await using (var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "files"))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.False(await recovery.CompleteDeletionAsync(accepted, deadline.Token));
            var unchanged = Assert.Single(await files.PendingDeletionsAsync(DateTimeOffset.UtcNow, 10));
            Assert.Equal(3, unchanged.Version);
            Assert.Null(unchanged.BytesRemovedAt);
            await capacityLock.ReleaseAsync();
        }
        Assert.True(await recovery.CompleteDeletionAsync(accepted));
        Assert.Empty(await files.PendingDeletionsAsync(DateTimeOffset.UtcNow, 10));
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        Assert.Equal(4, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task UnrelatedDatabaseFailure_IsNotReportedAsOwnedCapacityContention()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new AdmissionApp(database.ConnectionString);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "admission-root-password");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var fault = new NpgsqlCommand("""
            CREATE FUNCTION platform.unrelated_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = 'identity_fact_capacity_busy', MESSAGE = 'private test failure'; END $$;
            CREATE TRIGGER unrelated_failure BEFORE INSERT ON platform.outbox FOR EACH ROW EXECUTE FUNCTION platform.unrelated_failure();
            """, connection))
        { await fault.ExecuteNonQueryAsync(); }
        var path = new Uri("/api/platform/settings/unrelated.failure", UriKind.Relative);
        try
        {
            using var rejected = await client.PutAsJsonAsync(path, new { value = "uncommitted" });
            Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
            var body = await rejected.Content.ReadAsStringAsync();
            Assert.DoesNotContain("audit_capacity.busy", body, StringComparison.Ordinal);
            Assert.DoesNotContain("private test failure", body, StringComparison.Ordinal);
            using var absent = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
            var missing = await absent.Content.ReadApiDataAsync();
            Assert.Equal(0, missing.GetProperty("version").ReadHttpInt64());
            Assert.Equal(JsonValueKind.Null, missing.GetProperty("value").ValueKind);
        }
        finally
        {
            await using var restore = new NpgsqlCommand("DROP TRIGGER unrelated_failure ON platform.outbox; DROP FUNCTION platform.unrelated_failure()", connection);
            await restore.ExecuteNonQueryAsync();
        }
        using var recovered = await client.PutAsJsonAsync(path, new { value = "recovered" });
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
    }

    private sealed class AdmissionApp(string connectionString, IPermissionCache? permissions = null, string waitTimeout = "00:00:00.250") : PersistentIdentityApp(connectionString,
        "admission-root-password", schedulingWorkerEnabled: false)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (permissions is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton(permissions));
            }
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new[] { "Platform", "Identity", "Files", "Scheduling" }.SelectMany(owner => new Dictionary<string, string?>
                {
                    [$"{owner}:AuditDelivery:CapacityWrite:Timeout"] = waitTimeout,
                    [$"{owner}:AuditDelivery:Cleanup:Enabled"] = "false",
                })));
            return base.CreateHost(builder);
        }
    }
}
