using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FilesFactCapacityPolicyTests
{
    [PostgresFact]
    public async Task FilesPostgres_UnrelatedReceiptLockError_RollsBackPolicyAndPreservesFileFacts()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var file = StoredFile.Register(new StoredFileId(99872), FileName.Create("persistent-fault.bin").Value,
            "application/octet-stream", "owner", now).Value;
        Assert.True(file.MarkStored("persistent-fault-storage", 3).IsSuccess);
        await files.SaveAsync(file);
        var committed = await files.FindAsync(file.Id);
        Assert.NotNull(committed);
        var before = (await policies.ReadPolicyAsync()).Value;
        var facts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, before.RetainedRecords);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var arrange = new NpgsqlCommand("""
            CREATE FUNCTION files.fail_policy_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION USING ERRCODE = '55P03', MESSAGE = 'Controlled receipt fault'; END $$;
            CREATE TRIGGER fail_policy_receipt BEFORE INSERT ON files.fact_policy_receipts
                FOR EACH ROW EXECUTE FUNCTION files.fail_policy_receipt();
            """, connection))
        {
            await arrange.ExecuteNonQueryAsync();
        }
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 100001, before.MaxPayloadBytes,
            before.MaxRecordPayloadBytes, "operator-adjustment");
        var error = await Assert.ThrowsAsync<PostgresException>(() => policies.AdjustAsync(request, "test-operator", now, null));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(facts, await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var unchanged = await files.FindAsync(file.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(committed.Version, unchanged.Version);
        Assert.Equal(committed.CreatedAt, unchanged.CreatedAt);
        Assert.Equal(committed.UpdatedAt, unchanged.UpdatedAt);
        await using (var repair = new NpgsqlCommand("DROP TRIGGER fail_policy_receipt ON files.fact_policy_receipts; DROP FUNCTION files.fail_policy_receipt();", connection))
        {
            await repair.ExecuteNonQueryAsync();
        }
        var retry = await policies.AdjustAsync(request, "test-operator", now, null);
        Assert.True(retry.IsSuccess);
        Assert.Equal(2, retry.Value.PolicyRevision);
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, after.RetainedRecords);
        Assert.Equal(1, after.ControlCapacity.RetainedRecords);
        var acceptedFacts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.All(facts, fact => Assert.Contains(fact, acceptedFacts));
        Assert.Single(acceptedFacts, entry => entry.Id == retry.Value.EventId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesMemory_SerializationFailureOrCancellation_PreservesFileAndPolicyAndAllowsRetry(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var rejecting = new RejectingEventSerializer(serializer)
        { ShouldReject = fact => fact.EventName == "files.fact-capacity-policy-changed.v1" };
        var canceling = new CancelingEventSerializer(serializer)
        {
            ShouldCancel = fact => fact.EventName == "files.fact-capacity-policy-changed.v1",
            CancelOnSerialize = cancellation,
        };
        IIntegrationEventSerializer selected = cancel ? canceling : rejecting;
        await using var baseApp = new PolicyApp { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(selected)));
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var file = StoredFile.Register(new StoredFileId(99871), FileName.Create("failure-policy.bin").Value,
            "application/octet-stream", "owner", now).Value;
        Assert.True(file.MarkStored("failure-policy-storage", 3).IsSuccess);
        await files.SaveAsync(file);
        var before = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, before.RetainedRecords);
        var facts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 4, before.MaxPayloadBytes,
            before.MaxRecordPayloadBytes, "operator-adjustment");
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policies.AdjustAsync(request, "test-operator", now, null, cancellation.Token));
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => policies.AdjustAsync(request, "test-operator", now, null));
            Assert.Equal("测试事实序列化故障。", error.Message);
        }
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(facts, await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var unchanged = await files.FindAsync(file.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(file.Version, unchanged.Version);
        Assert.Equal(file.CreatedAt, unchanged.CreatedAt);
        Assert.Equal(file.UpdatedAt, unchanged.UpdatedAt);
        rejecting.ShouldReject = null;
        canceling.CancelOnSerialize = null;
        var retry = await policies.AdjustAsync(request, "test-operator", now, null);
        Assert.True(retry.IsSuccess);
        Assert.Equal(2, retry.Value.PolicyRevision);
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, after.RetainedRecords);
        Assert.Equal(1, after.ControlCapacity.RetainedRecords);
        var acceptedFacts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.All(facts, fact => Assert.Contains(fact, acceptedFacts));
        Assert.Single(acceptedFacts, entry => entry.Id == retry.Value.EventId);
    }

    [Fact]
    public async Task FilesMemory_ControlCleanupRequiresReceiptAndDeliveryDeadlines_AndKeepsDeadLetters()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await AssertControlCleanupAsync(scope.ServiceProvider);
    }

    [PostgresFact]
    public async Task FilesPostgres_ControlCleanupRequiresReceiptAndDeliveryDeadlines_AndKeepsDeadLetters()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await AssertControlCleanupAsync(scope.ServiceProvider);
    }

    private static async Task AssertControlCleanupAsync(IServiceProvider services)
    {
        var policies = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
        var cleanup = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("files");
        var outbox = services.GetRequiredKeyedService<IOutboxStore>("files");
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var initial = (await policies.ReadPolicyAsync()).Value;
        var firstRequest = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, initial.MaxRecords + 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
        var first = (await policies.AdjustAsync(firstRequest, "test-operator", now, null)).Value;
        var secondRequest = firstRequest with { RequestId = Guid.NewGuid(), ExpectedPolicyRevision = 2, MaxRecords = initial.MaxRecords + 2 };
        var second = (await policies.AdjustAsync(secondRequest, "test-operator", now, null)).Value;
        var noOpRequest = secondRequest with { RequestId = Guid.NewGuid(), ExpectedPolicyRevision = 3 };
        var noOp = (await policies.AdjustAsync(noOpRequest, "test-operator", now, null)).Value;
        Assert.False(noOp.Changed);
        Assert.Null(noOp.EventId);
        Assert.Equal(3, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        Assert.True(await outbox.MarkDeadLetteredAsync(second.EventId!.Value, "test-failure", now, 0));
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(7).AddTicks(-1)));
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(7)));
        Assert.Equal(2, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        await outbox.MarkDeliveredAsync(first.EventId!.Value, now.AddDays(7));
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(7).AddHours(23)));
        Assert.Equal(first, (await policies.AdjustAsync(firstRequest, "test-operator", now.AddDays(7).AddHours(23), null)).Value);
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(8)));
        var stale = await policies.AdjustAsync(firstRequest, "test-operator", now.AddDays(8), null);
        Assert.True(stale.IsFailure);
        Assert.Equal("files.audit_policy.conflict", stale.Error.Code);
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(30)));
        Assert.Equal(second, (await policies.AdjustAsync(secondRequest, "test-operator", now.AddDays(30), null)).Value);
        await outbox.MarkDeliveredAsync(second.EventId.Value, now.AddDays(30));
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(31)));
        var released = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(0, released.ControlCapacity.RetainedRecords);
        Assert.Equal(0, released.ControlCapacity.RetainedPayloadBytes);
        Assert.Equal(3, released.PolicyRevision);
        Assert.Equal(initial.RetainedRecords, released.RetainedRecords);
        Assert.Equal(initial.RetainedPayloadBytes, released.RetainedPayloadBytes);
        // A safely removed request identity is a new CAS attempt, with a new event identity.
        var reused = await policies.AdjustAsync(firstRequest with { ExpectedPolicyRevision = 3 }, "test-operator", now.AddDays(31), null);
        Assert.True(reused.IsSuccess);
        Assert.Equal(4, reused.Value.PolicyRevision);
        Assert.NotEqual(first.EventId, reused.Value.EventId);
        Assert.NotEqual(firstRequest.RequestId, reused.Value.EventId);
        Assert.Equal(reused.Value.EventId, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Id);
    }

    [PostgresFact]
    public async Task FilesPostgres_PolicyExpansionAndReceiptSurviveHostRecreation_WithoutChangingStoredBytes()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var path = new Uri("/api/files/audit-capacity", UriKind.Relative);
        var expansion = new FactCapacityPolicyRequest(Guid.NewGuid(), 2, 4, 268435456, 16384, "operator-adjustment");
        string receipt;
        long id;
        long version;
        DateTimeOffset? updatedAt;
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization;
        await using (var app = new PersistentIdentityApp(database.ConnectionString, "files-policy-root", schedulingWorkerEnabled: false))
        {
            using var client = app.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-policy-root");
            authorization = client.DefaultRequestHeaders.Authorization;
            using var reduced = await client.PutAsJsonAsync(path, expansion with
            { RequestId = Guid.NewGuid(), ExpectedPolicyRevision = 1, MaxRecords = 2 });
            Assert.Equal(HttpStatusCode.OK, reduced.StatusCode);
            Assert.Equal(2, (await reduced.Content.ReadApiDataAsync()).GetProperty("policyRevision").ReadHttpInt64());
            using var bytes = new ByteArrayContent([7, 8, 9]);
            using var uploaded = await client.PostAsync(new Uri("/api/files?name=persistent-policy.bin", UriKind.Relative), bytes);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
            await using var scope = app.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
            var file = await repository.FindAsync(new StoredFileId(id));
            Assert.NotNull(file);
            version = file.Version;
            updatedAt = file.UpdatedAt;
            using var refusedBytes = new ByteArrayContent([10]);
            using var refused = await client.PostAsync(new Uri("/api/files?name=refused-policy.bin", UriKind.Relative), refusedBytes);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            using var expanded = await client.PutAsJsonAsync(path, expansion);
            Assert.Equal(HttpStatusCode.OK, expanded.StatusCode);
            var accepted = await expanded.Content.ReadApiDataAsync();
            receipt = accepted.GetRawText();
            Assert.Equal(3, accepted.GetProperty("policyRevision").ReadHttpInt64());
            using var diagnostic = await client.GetAsync(path);
            var snapshot = await diagnostic.Content.ReadApiDataAsync();
            Assert.True(snapshot.GetProperty("isPersistent").GetBoolean());
            Assert.Equal(2, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
            Assert.Equal(2, snapshot.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
            var unchanged = await repository.FindAsync(file.Id);
            Assert.NotNull(unchanged);
            Assert.Equal(version, unchanged.Version);
            Assert.Equal(updatedAt, unchanged.UpdatedAt);
        }
        // Host recreation is in this test process; real OS process restart has a separate qualification.
        await using var recreated = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var restored = recreated.CreateClient();
        restored.DefaultRequestHeaders.Authorization = authorization;
        using var replay = await restored.PutAsJsonAsync(path, expansion);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, (await replay.Content.ReadApiDataAsync()).GetRawText());
        await using var observer = recreated.Services.CreateAsyncScope();
        var stored = await observer.ServiceProvider.GetRequiredService<IStoredFileRepository>().FindAsync(new StoredFileId(id));
        Assert.NotNull(stored);
        Assert.Equal(version, stored.Version);
        Assert.Equal(updatedAt, stored.UpdatedAt);
        using var downloaded = await restored.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(new byte[] { 7, 8, 9 }, await downloaded.Content.ReadAsByteArrayAsync());
        using var retryBytes = new ByteArrayContent([10]);
        using var retry = await restored.PostAsync(new Uri("/api/files?name=refused-policy.bin", UriKind.Relative), retryBytes);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var final = (await observer.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files").ReadPolicyAsync()).Value;
        Assert.Equal(4, final.RetainedRecords);
        Assert.Equal(2, final.ControlCapacity.RetainedRecords);
        Assert.Equal(3, final.PolicyRevision);
    }

    [Fact]
    public async Task FilesMemory_FullBusinessCapacityCanExpand_WithoutChangingExistingFileOrBytes()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var uploaded = await client.PostAsync(new Uri("/api/files?name=policy-first.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        await using var scope = app.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var file = await repository.FindAsync(new StoredFileId(id));
        Assert.NotNull(file);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var originalFacts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, originalFacts.Count);
        using var refusedBytes = new ByteArrayContent([4, 5, 6]);
        using var refused = await client.PostAsync(new Uri("/api/files?name=policy-second.bin", UriKind.Relative), refusedBytes);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(originalFacts, await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var requestId = Guid.NewGuid();
        var request = new
        {
            requestId,
            expectedPolicyRevision = "1",
            maxRecords = "4",
            maxPayloadBytes = "268435456",
            maxRecordPayloadBytes = 16384,
            reason = "operator-adjustment"
        };
        using var expanded = await client.PutAsJsonAsync(new Uri("/api/files/audit-capacity", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, expanded.StatusCode);
        var receipt = await expanded.Content.ReadApiDataAsync();
        Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());
        Assert.True(receipt.GetProperty("changed").GetBoolean());
        var eventId = receipt.GetProperty("eventId").GetGuid();
        Assert.NotEqual(requestId, eventId);
        var facts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.All(originalFacts, fact => Assert.Contains(fact, facts));
        var control = Assert.Single(facts, fact => fact.Id == eventId);
        Assert.Equal("files.fact-capacity-policy-changed.v1", control.EventName);
        using var payload = JsonDocument.Parse(control.Payload);
        var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindByUserNameAsync(UserName.Create(PlatformAppWithRootAccount.RootUserName).Value);
        Assert.NotNull(user);
        Assert.Equal(user.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            payload.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("previous").GetProperty("maxRecords").GetInt64());
        Assert.Equal(4, payload.RootElement.GetProperty("current").GetProperty("maxRecords").GetInt64());
        var unchanged = await repository.FindAsync(file.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(file.Version, unchanged.Version);
        Assert.Equal(file.CreatedAt, unchanged.CreatedAt);
        Assert.Equal(file.UpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(file.StorageKey, unchanged.StorageKey);
        using var downloaded = await client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await downloaded.Content.ReadAsByteArrayAsync());
        using var replay = await client.PutAsJsonAsync(new Uri("/api/files/audit-capacity", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        Assert.Equal(facts, await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        using var retryBytes = new ByteArrayContent([4, 5, 6]);
        using var accepted = await client.PostAsync(new Uri("/api/files?name=policy-second.bin", UriKind.Relative), retryBytes);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var finalResponse = await client.GetAsync(new Uri("/api/files/audit-capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, finalResponse.StatusCode);
        var final = await finalResponse.Content.ReadApiDataAsync();
        Assert.Equal("files", final.GetProperty("context").GetString());
        Assert.Equal(4, final.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(1, final.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
    }

    private sealed class PolicyApp : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:AuditDelivery:MemoryCapacity:MaxRecords"] = "2",
                ["Files:AuditDelivery:Cleanup:Enabled"] = "false",
                ["Files:StorageRoot"] = Path.Combine(Path.GetTempPath(), "nsn-policy-files-" + Guid.NewGuid().ToString("N")),
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Root:UserName"] = PlatformAppWithRootAccount.RootUserName,
                ["Identity:Root:Password"] = PlatformAppWithRootAccount.RootPassword,
            }));
        }
    }
}
