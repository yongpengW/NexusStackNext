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
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityFactCapacityPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityMemory_SerializationFailureOrCancellation_PublishesNoPartialPolicyAndAllowsRetry(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var rejecting = new RejectingEventSerializer(serializer)
        { ShouldReject = fact => fact.EventName == "identity.fact-capacity-policy-changed.v1" };
        var canceling = new CancelingEventSerializer(serializer)
        {
            ShouldCancel = fact => fact.EventName == "identity.fact-capacity-policy-changed.v1",
            CancelOnSerialize = cancellation,
        };
        IIntegrationEventSerializer selected = cancel ? canceling : rejecting;
        await using var baseApp = new PolicyApp { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(selected)));
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("identity");
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var before = (await policies.ReadPolicyAsync()).Value;
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
        rejecting.ShouldReject = null;
        canceling.CancelOnSerialize = null;
        var retry = await policies.AdjustAsync(request, "test-operator", now, null);
        Assert.True(retry.IsSuccess);
        Assert.Equal(2, retry.Value.PolicyRevision);
        Assert.Equal(1, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue), entry => entry.Id == retry.Value.EventId);
    }

    [PostgresFact]
    public async Task IdentityPostgres_UnrelatedReceiptLockError_RollsBackPolicyAndNeverFlushesPendingBusinessWork()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("identity");
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var before = (await policies.ReadPolicyAsync()).Value;
        var code = RoleCode.Create("pending-policy-role").Value;
        await scope.ServiceProvider.GetRequiredService<IRoleRepository>().AddAsync(
            Role.Create(new RoleId(99891), code, RoleName.Create("Pending role").Value));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var arrange = new NpgsqlCommand("""
            CREATE FUNCTION identity.fail_policy_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION USING ERRCODE = '55P03', MESSAGE = 'Controlled receipt fault'; END $$;
            CREATE TRIGGER fail_policy_receipt BEFORE INSERT ON identity.fact_policy_receipts
                FOR EACH ROW EXECUTE FUNCTION identity.fail_policy_receipt();
            """, connection))
        {
            await arrange.ExecuteNonQueryAsync();
        }
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 100001, before.MaxPayloadBytes,
            before.MaxRecordPayloadBytes, "operator-adjustment");
        var error = await Assert.ThrowsAsync<PostgresException>(() => policies.AdjustAsync(request, "test-operator", now, null));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        await using (var observer = app.Services.CreateAsyncScope())
        {
            Assert.Null(await observer.ServiceProvider.GetRequiredService<IRoleRepository>().FindByCodeAsync(code));
        }
        await using (var repair = new NpgsqlCommand("DROP TRIGGER fail_policy_receipt ON identity.fact_policy_receipts; DROP FUNCTION identity.fail_policy_receipt();", connection))
        {
            await repair.ExecuteNonQueryAsync();
        }
        var retry = await policies.AdjustAsync(request, "test-operator", now, null);
        Assert.True(retry.IsSuccess);
        Assert.Equal(2, retry.Value.PolicyRevision);
        await using (var observer = app.Services.CreateAsyncScope())
        {
            Assert.Null(await observer.ServiceProvider.GetRequiredService<IRoleRepository>().FindByCodeAsync(code));
        }
        // Only the explicit owned unit of work is allowed to publish the pending role.
        await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        await using var committed = app.Services.CreateAsyncScope();
        var role = await committed.ServiceProvider.GetRequiredService<IRoleRepository>().FindByCodeAsync(code);
        Assert.NotNull(role);
        Assert.Equal(1, role.Version);
        var final = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1, final.RetainedRecords);
        Assert.Equal(1, final.ControlCapacity.RetainedRecords);
        Assert.Equal(2, final.PolicyRevision);
    }

    [Fact]
    public async Task IdentityMemory_ControlCleanupRequiresReceiptAndDeliveryDeadlines_AndKeepsDeadLetters()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await AssertControlCleanupAsync(scope.ServiceProvider);
    }

    [PostgresFact]
    public async Task IdentityPostgres_ControlCleanupRequiresReceiptAndDeliveryDeadlines_AndKeepsDeadLetters()
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
        var policies = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("identity");
        var cleanup = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("identity");
        var outbox = services.GetRequiredKeyedService<IOutboxStore>("identity");
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
        // A no-op receipt expires independently; pending and dead-lettered facts cannot release a slot.
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(7)));
        Assert.Equal(2, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        await outbox.MarkDeliveredAsync(first.EventId!.Value, now.AddDays(7));
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(7).AddHours(23)));
        Assert.Equal(first, (await policies.AdjustAsync(firstRequest, "test-operator", now.AddDays(7).AddHours(23), null)).Value);
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(8)));
        var stale = await policies.AdjustAsync(firstRequest, "test-operator", now.AddDays(8), null);
        Assert.True(stale.IsFailure);
        Assert.Equal(IdentityFactCapacityPolicyErrors.Conflict, stale.Error);
        var current = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1, current.ControlCapacity.RetainedRecords);
        Assert.Equal(3, current.PolicyRevision);
        Assert.Equal(initial.RetainedRecords, current.RetainedRecords);
        Assert.Equal(initial.RetainedPayloadBytes, current.RetainedPayloadBytes);
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(30)));
        Assert.Equal(second, (await policies.AdjustAsync(secondRequest, "test-operator", now.AddDays(30), null)).Value);
        await outbox.MarkDeliveredAsync(second.EventId.Value, now.AddDays(30));
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(31)));
        var released = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(0, released.ControlCapacity.RetainedRecords);
        Assert.Equal(0, released.ControlCapacity.RetainedPayloadBytes);
        Assert.Equal(3, released.PolicyRevision);
        Assert.DoesNotContain(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue),
            entry => entry.EventName == "identity.fact-capacity-policy-changed.v1");
    }

    [PostgresFact]
    public async Task IdentityPostgres_PolicyAndReceiptSurviveHostRecreation_WithoutCommittingBusinessState()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var requestId = Guid.NewGuid();
        var request = new
        {
            requestId,
            expectedPolicyRevision = "2",
            maxRecords = "4",
            maxPayloadBytes = "268435456",
            maxRecordPayloadBytes = 16384,
            reason = "operator-adjustment"
        };
        string receiptJson;
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization;
        long userId;
        long version;
        long sessionVersion;
        await using (var app = new PersistentIdentityApp(database.ConnectionString, "policy-identity-root-password", schedulingWorkerEnabled: false))
        {
            using var client = app.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "policy-identity-root-password");
            authorization = client.DefaultRequestHeaders.Authorization;
            await using var scope = app.Services.CreateAsyncScope();
            var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().FindByUserNameAsync(UserName.Create("journey-root").Value);
            Assert.NotNull(user);
            userId = user.Id.Value;
            version = user.Version;
            sessionVersion = user.SessionVersion;
            var cache = scope.ServiceProvider.GetRequiredService<IPermissionCache>();
            var permissions = await cache.GetAsync(user.Id);
            Assert.True(permissions.IsSuccess);
            using var limited = await client.PutAsJsonAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative),
                new
                {
                    requestId = Guid.NewGuid(),
                    expectedPolicyRevision = "1",
                    maxRecords = "3",
                    maxPayloadBytes = "268435456",
                    maxRecordPayloadBytes = 16384,
                    reason = "operator-adjustment"
                });
            Assert.Equal(HttpStatusCode.OK, limited.StatusCode);
            using var refused = await client.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
                new { code = "pg-policy-role", name = "Capacity role" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            using var expanded = await client.PutAsJsonAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.OK, expanded.StatusCode);
            var receipt = await expanded.Content.ReadApiDataAsync();
            Assert.Equal(3, receipt.GetProperty("policyRevision").ReadHttpInt64());
            receiptJson = receipt.GetRawText();
            Assert.Same(permissions.Value, (await cache.GetAsync(user.Id)).Value);
            var facts = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity").ReadPendingAsync(10, DateTimeOffset.MaxValue);
            Assert.Equal(2, facts.Count(fact => fact.EventName == "identity.fact-capacity-policy-changed.v1"));
            Assert.Equal(3, facts.Count(fact => fact.EventName == "identity.entity-committed.v1"));
        }
        await using var recreated = new PersistentIdentityApp(database.ConnectionString, "policy-identity-root-password", schedulingWorkerEnabled: false);
        using var replayClient = recreated.CreateClient();
        replayClient.DefaultRequestHeaders.Authorization = authorization;
        using var replay = await replayClient.PutAsJsonAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receiptJson, (await replay.Content.ReadApiDataAsync()).GetRawText());
        using var diagnostic = await replayClient.GetAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, diagnostic.StatusCode);
        var after = await diagnostic.Content.ReadApiDataAsync();
        Assert.Equal(3, after.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(3, after.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(2, after.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        await using var observer = recreated.Services.CreateAsyncScope();
        var unchanged = await observer.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new(userId));
        Assert.NotNull(unchanged);
        Assert.Equal(version, unchanged.Version);
        Assert.Equal(sessionVersion, unchanged.SessionVersion);
        using var accepted = await replayClient.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
            new { code = "pg-policy-role", name = "Capacity role" });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task IdentityMemory_FullBusinessCapacityCanExpand_WithoutChangingUserOrInvalidatingPermissions()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await using var scope = app.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindByUserNameAsync(UserName.Create(PlatformAppWithRootAccount.RootUserName).Value);
        Assert.NotNull(user);
        var version = user.Version;
        var sessionVersion = user.SessionVersion;
        var updatedAt = user.UpdatedAt;
        var cache = scope.ServiceProvider.GetRequiredService<IPermissionCache>();
        var permissions = await cache.GetAsync(user.Id);
        Assert.True(permissions.IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var originalFacts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(3, originalFacts.Count);
        using var beforeResponse = await client.GetAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
        var before = await beforeResponse.Content.ReadApiDataAsync();
        Assert.Equal(3, before.GetProperty("retainedRecords").ReadHttpInt64());
        using var refused = await client.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
            new { code = "policy-capacity-role", name = "Capacity role" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
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
        using var expanded = await client.PutAsJsonAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, expanded.StatusCode);
        var receipt = await expanded.Content.ReadApiDataAsync();
        Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());
        Assert.True(receipt.GetProperty("changed").GetBoolean());
        var eventId = receipt.GetProperty("eventId").GetGuid();
        Assert.NotEqual(requestId, eventId);
        var after = await (await client.GetAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative))).Content.ReadApiDataAsync();
        Assert.Equal(4, after.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(3, after.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(1, after.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        var facts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.All(originalFacts, fact => Assert.Contains(fact, facts));
        var control = Assert.Single(facts, fact => fact.Id == eventId);
        Assert.Equal("identity.fact-capacity-policy-changed.v1", control.EventName);
        using var payload = JsonDocument.Parse(control.Payload);
        Assert.Equal(user.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), payload.RootElement.GetProperty("actorId").GetString());
        await using var observer = app.Services.CreateAsyncScope();
        var unchanged = await observer.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(user.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(version, unchanged.Version);
        Assert.Equal(sessionVersion, unchanged.SessionVersion);
        Assert.Equal(updatedAt, unchanged.UpdatedAt);
        Assert.Same(permissions.Value, (await cache.GetAsync(user.Id)).Value);
        using var replay = await client.PutAsJsonAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        Assert.Equal(facts, await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        using var accepted = await client.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
            new { code = "policy-capacity-role", name = "Capacity role" });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var final = await (await client.GetAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative))).Content.ReadApiDataAsync();
        Assert.Equal(4, final.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(1, final.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
    }

    private sealed class PolicyApp : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:AuditDelivery:MemoryCapacity:MaxRecords"] = "3",
                ["Identity:AuditDelivery:Cleanup:Enabled"] = "false",
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
