using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Endpoints;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(PlatformJourneyDefinition.Name)]
public sealed class FactCapacityPolicyTests(PlatformJourneyTemplate databases)
{
    [Fact]
    public async Task PlatformPolicyRequest_OpenApi_OnlyPublishesAcceptedInputs()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var schema = document.GetProperty("paths").GetProperty("/api/platform/audit-capacity")
            .GetProperty("put").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        var properties = HttpInt64OpenApiTests.Resolve(document, schema).GetProperty("properties");
        Assert.Equal(new[] { "expectedPolicyRevision", "maxPayloadBytes", "maxRecordPayloadBytes", "maxRecords", "reason", "requestId" },
            properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PlatformMemory_ConcurrentPolicyChanges_ReplayOriginalReceiptAndRejectAbaOrIdentityReuse()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        await AssertConditionalPolicyProtocolAsync(scope.ServiceProvider);
    }

    [PostgresFact]
    public async Task PlatformPostgres_ConcurrentPolicyChanges_ReplayOriginalReceiptAndRejectAbaOrIdentityReuse()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "policy-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await AssertConditionalPolicyProtocolAsync(scope.ServiceProvider);
    }

    [Fact]
    public async Task PlatformMemory_ControlPoolFull_RefusesChangesWithoutBlockingBusinessAndRecoversAfterBoundedCleanup()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("platform");
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 1000; index++)
        {
            var accepted = await policies.AdjustAsync(new(Guid.NewGuid(), 1, 1, 16384, 16384, "operator-adjustment"),
                "test-operator", now, null);
            Assert.True(accepted.IsSuccess);
            Assert.False(accepted.Value.Changed);
        }
        var before = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1000, before.ControlCapacity.RetainedRecords);
        Assert.Equal(1, before.PolicyRevision);
        Assert.Equal(0, before.RetainedRecords);
        var expansion = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2, 16384, 16384, "operator-adjustment");
        var refused = await policies.AdjustAsync(expansion, "test-operator", now, null);
        Assert.True(refused.IsFailure);
        Assert.Equal(SettingFactCapacityPolicyErrors.Exhausted, refused.Error);
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        Assert.Empty(await outbox.ReadPendingAsync(10, now));
        using var business = await client.PutAsJsonAsync(new Uri("/api/platform/settings/control.first", UriKind.Relative), new { value = "allowed" });
        Assert.Equal(HttpStatusCode.NoContent, business.StatusCode);
        Assert.Equal(1, (await policies.ReadPolicyAsync()).Value.RetainedRecords);
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("platform");
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(7)));
        Assert.Equal(999, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        var recovered = await policies.AdjustAsync(expansion, "test-operator", now.AddDays(7), null);
        Assert.True(recovered.IsSuccess);
        Assert.True(recovered.Value.Changed);
        Assert.Equal(2, recovered.Value.PolicyRevision);
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1000, after.ControlCapacity.RetainedRecords);
        Assert.Equal(1, after.RetainedRecords);
        using var secondBusiness = await client.PutAsJsonAsync(new Uri("/api/platform/settings/control.second", UriKind.Relative), new { value = "allowed" });
        Assert.Equal(HttpStatusCode.NoContent, secondBusiness.StatusCode);
        var pending = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(3, pending.Count);
        Assert.Single(pending, entry => entry.EventName == "platform.fact-capacity-policy-changed.v1");
    }

    [PostgresFact]
    public async Task PlatformPolicyMaintenance_ExposesRollbackFailureThenReleasesBatchesAfterRecovery()
    {
        await using var database = await databases.CreateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var fault = connection.CreateCommand())
        {
            fault.CommandText = """
                CREATE FUNCTION platform.reject_policy_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."EventName" = 'platform.fact-capacity-policy-changed.v1' THEN
                        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Controlled policy cleanup failure.';
                    END IF;
                    RETURN OLD;
                END $$;
                CREATE TRIGGER reject_policy_cleanup BEFORE DELETE ON platform.outbox
                    FOR EACH ROW EXECUTE FUNCTION platform.reject_policy_cleanup();
                """;
            await fault.ExecuteNonQueryAsync();
        }
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        using var host = new HostBuilder().ConfigureServices((context, services) =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:Storage:Provider"] = "Postgres",
                ["ConnectionStrings:Platform"] = database.ConnectionString,
                ["Platform:AuditDelivery:Cleanup:Enabled"] = "false",
                ["Platform:AuditDelivery:PolicyMaintenance:Enabled"] = "true",
                ["Platform:AuditDelivery:PolicyMaintenance:BatchSize"] = "1",
                ["Platform:AuditDelivery:PolicyMaintenance:Interval"] = "00:00:01",
                ["Platform:AuditDelivery:PolicyMaintenance:Timeout"] = "00:00:01",
            }).Build();
            services.AddLogging();
            services.AddSingleton<IClock>(new FixedClock(now.AddDays(7)));
            services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
            services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
            services.AddPlatformModule(configuration, context.HostingEnvironment);
        }).Build();
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("platform");
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
            var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000001"),
                1, 2, 16384, 16384, "operator-adjustment");
            var accepted = await policies.AdjustAsync(request, "test-operator", now, null);
            Assert.True(accepted.IsSuccess);
            Assert.NotNull(accepted.Value.EventId);
            await outbox.MarkDeliveredAsync(accepted.Value.EventId.Value, now);
            var noOpRequest = request with { RequestId = Guid.Parse("00000000-0000-0000-0000-000000000002"), ExpectedPolicyRevision = 2 };
            var noOp = await policies.AdjustAsync(noOpRequest, "test-operator", now, null);
            Assert.True(noOp.IsSuccess);
            var before = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(2, before.ControlCapacity.RetainedRecords);
            var health = host.Services.GetRequiredService<HealthCheckService>();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            HealthReportEntry diagnostic;
            do
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "platform-policy-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if (diagnostic.Status != HealthStatus.Degraded) { await Task.Delay(50, budget.Token); }
            } while (diagnostic.Status != HealthStatus.Degraded);
            Assert.True((long)diagnostic.Data["cleanupFailures"] > 0);
            Assert.DoesNotContain("Controlled policy cleanup failure", diagnostic.Description!, StringComparison.Ordinal);
            Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
            Assert.Equal(accepted.Value, (await policies.AdjustAsync(request, "test-operator", now.AddDays(7), null)).Value);
            Assert.Equal(noOp.Value, (await policies.AdjustAsync(noOpRequest, "test-operator", now.AddDays(7), null)).Value);
            await using (var recover = connection.CreateCommand())
            {
                recover.CommandText = "DROP TRIGGER reject_policy_cleanup ON platform.outbox";
                await recover.ExecuteNonQueryAsync();
            }
            do
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "platform-policy-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["releasedRequests"] != 2) { await Task.Delay(50, budget.Token); }
            } while ((long)diagnostic.Data["releasedRequests"] != 2);
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.False((bool)diagnostic.Data["cleanupDegraded"]);
            Assert.True((long)diagnostic.Data["cleanupRuns"] >= 2);
            var after = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(0, after.ControlCapacity.RetainedRecords);
            Assert.Equal(0, after.ControlCapacity.RetainedPayloadBytes);
            Assert.Equal(before.PolicyRevision, after.PolicyRevision);
            Assert.Equal(before.Business, after.Business);
        }
        finally { await host.StopAsync(); }
    }

    [PostgresFact]
    public async Task PlatformPostgres_AcceptedPolicyEvidence_IsImmutableWhileDeliveryMetadataCanChange()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "policy-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("platform");
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2, 16384, 16384, "operator-adjustment");
        var accepted = await policies.AdjustAsync(request, "test-operator", now, null);
        Assert.True(accepted.IsSuccess);
        var original = Assert.Single(await outbox.ReadPendingAsync(10, now));
        var before = (await policies.ReadPolicyAsync()).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var assignment in new[]
        {
            "\"Payload\" = 'tampered'", "\"OccurredAt\" = \"OccurredAt\" + interval '1 second'",
            "\"EventName\" = 'test.mutable'", "\"Id\" = @replacement",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE platform.outbox SET {assignment} WHERE \"Id\" = @id";
            command.Parameters.AddWithValue("id", original.Id);
            command.Parameters.AddWithValue("replacement", Guid.NewGuid());
            var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal("platform_fact_policy_immutable", failure.ConstraintName);
        }
        foreach (var assignment in new[]
        {
            "\"RecordJson\" = '{}'", "\"RetainUntil\" = \"RetainUntil\" - interval '1 day'",
            "\"PayloadBytes\" = \"PayloadBytes\" + 1", "\"RequestId\" = @replacement", "\"EventId\" = NULL",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE platform.fact_policy_receipts SET {assignment} WHERE \"RequestId\" = @id";
            command.Parameters.AddWithValue("id", request.RequestId);
            command.Parameters.AddWithValue("replacement", Guid.NewGuid());
            var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal("platform_fact_policy_receipt_immutable", failure.ConstraintName);
        }
        Assert.Equal(original, Assert.Single(await outbox.ReadPendingAsync(10, now)));
        Assert.Equal(accepted.Value, (await policies.AdjustAsync(request, "test-operator", now.AddDays(1), null)).Value);
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        Assert.True(await outbox.MarkFailedAsync(original.Id, "delivery-failed", now.AddMinutes(1), 0));
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "stopped", now, 0));
        await outbox.MarkDeliveredAsync(original.Id, now);
        Assert.Empty(await outbox.ReadPendingAsync(10, now.AddDays(1)));
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
    }

    [PostgresFact]
    public async Task PlatformPostgres_PolicyCleanup_RequiresBothRetentionsAndPreservesStoppedEvidence()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "policy-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("platform");
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2, 16384, 16384, "operator-adjustment");
        var first = await policies.AdjustAsync(request, "test-operator", now, null);
        Assert.True(first.IsSuccess);
        var stoppedRequest = request with { RequestId = Guid.NewGuid(), ExpectedPolicyRevision = 2, MaxRecords = 3 };
        var stopped = await policies.AdjustAsync(stoppedRequest, "test-operator", now, null);
        Assert.True(stopped.IsSuccess);
        var noOp = await policies.AdjustAsync(stoppedRequest with { RequestId = Guid.NewGuid(), ExpectedPolicyRevision = 3 },
            "test-operator", now, null);
        Assert.True(noOp.IsSuccess);
        Assert.False(noOp.Value.Changed);
        Assert.NotNull(first.Value.EventId);
        Assert.NotNull(stopped.Value.EventId);
        Assert.True(await outbox.MarkDeadLetteredAsync(stopped.Value.EventId.Value, "stopped", now, 0));
        var before = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(3, before.ControlCapacity.RetainedRecords);
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("platform");
        Assert.Equal(0, await cleanup.CleanupAsync(100, now.AddDays(7).AddTicks(-1)));
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        // The no-op is eligible; unconfirmed and dead-lettered facts remain after the receipt deadline.
        Assert.Equal(1, await cleanup.CleanupAsync(100, now.AddDays(7)));
        Assert.Equal(2, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        await outbox.MarkDeliveredAsync(first.Value.EventId.Value, now.AddDays(7));
        Assert.Equal(0, await cleanup.CleanupAsync(100, now.AddDays(8).AddTicks(-1)));
        Assert.Equal(1, await cleanup.CleanupAsync(100, now.AddDays(8)));
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1, after.ControlCapacity.RetainedRecords);
        Assert.InRange(after.ControlCapacity.RetainedPayloadBytes, 1, before.ControlCapacity.RetainedPayloadBytes - 1);
        Assert.Equal(before.PolicyRevision, after.PolicyRevision);
        Assert.Equal(before.Business, after.Business);
        Assert.Equal(stopped.Value, (await policies.AdjustAsync(stoppedRequest, "test-operator", now.AddDays(10), null)).Value);
        var reused = await policies.AdjustAsync(request, "test-operator", now.AddDays(10), null);
        Assert.True(reused.IsFailure);
        Assert.Equal(SettingFactCapacityPolicyErrors.Conflict, reused.Error);
        // A late genuine confirmation must still find the preserved dead letter and its linked receipt.
        await outbox.MarkDeliveredAsync(stopped.Value.EventId.Value, now.AddDays(10));
        Assert.Equal(0, await cleanup.CleanupAsync(100, now.AddDays(11).AddTicks(-1)));
        Assert.Equal(1, await cleanup.CleanupAsync(100, now.AddDays(11)));
        var empty = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(0, empty.ControlCapacity.RetainedRecords);
        Assert.Equal(0, empty.ControlCapacity.RetainedPayloadBytes);
        Assert.Equal(before.PolicyRevision, empty.PolicyRevision);
        Assert.Equal(before.Business, empty.Business);
        Assert.Empty(await outbox.ReadPendingAsync(10, now.AddDays(11)));
    }

    [Fact]
    public async Task PlatformMemory_NoOpReceipts_AreRetainedUntilDeadlineAndSafelyReleased()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("platform");
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var first = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000001"),
            1, 1, 16384, 16384, "operator-adjustment");
        var second = first with { RequestId = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var firstReceipt = await policies.AdjustAsync(first, "test-operator", now, null);
        var secondReceipt = await policies.AdjustAsync(second, "test-operator", now, null);
        Assert.True(firstReceipt.IsSuccess);
        Assert.True(secondReceipt.IsSuccess);
        Assert.False(firstReceipt.Value.Changed);
        Assert.Null(firstReceipt.Value.EventId);
        Assert.Equal(1, firstReceipt.Value.PolicyRevision);
        Assert.Equal(now.AddDays(7), firstReceipt.Value.RetainUntil);
        var before = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, before.ControlCapacity.RetainedRecords);
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("platform");
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(7).AddTicks(-1)));
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(7)));
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1, after.ControlCapacity.RetainedRecords);
        Assert.InRange(after.ControlCapacity.RetainedPayloadBytes, 1, before.ControlCapacity.RetainedPayloadBytes - 1);
        Assert.Equal(before.Business, after.Business);
        Assert.Equal(before.PolicyRevision, after.PolicyRevision);
        var acceptedAgain = await policies.AdjustAsync(first, "test-operator", now.AddDays(7), null);
        Assert.True(acceptedAgain.IsSuccess);
        Assert.Equal(now.AddDays(7), acceptedAgain.Value.AcceptedAt);
        Assert.Equal(secondReceipt.Value, (await policies.AdjustAsync(second, "test-operator", now.AddDays(7), null)).Value);
        Assert.Equal(1, await cleanup.CleanupAsync(100, now.AddDays(7)));
        Assert.Equal(1, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        Assert.Empty(await outbox.ReadPendingAsync(10, now.AddDays(7)));
    }

    [Fact]
    public async Task PlatformPolicyDiagnostics_OpenApi_PublishesRevisionAndExactControlCounters()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var schema = document.GetProperty("paths").GetProperty("/api/platform/audit-capacity")
            .GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        var envelope = HttpInt64OpenApiTests.Resolve(document, schema);
        var data = HttpInt64OpenApiTests.Resolve(document, envelope.GetProperty("properties").GetProperty("data")).GetProperty("properties");
        Assert.True(data.TryGetProperty("policyRevision", out var revision));
        HttpInt64OpenApiTests.AssertOutput(revision, nullable: false);
        Assert.True(data.TryGetProperty("controlCapacity", out var controlSchema));
        var control = HttpInt64OpenApiTests.Resolve(document, controlSchema).GetProperty("properties");
        foreach (var name in new[] { "maxRecords", "maxPayloadBytes", "retainedRecords", "retainedPayloadBytes" })
        {
            HttpInt64OpenApiTests.AssertOutput(control.GetProperty(name), nullable: false);
        }
        Assert.False(data.TryGetProperty("business", out _));
    }

    [PostgresFact]
    public async Task PlatformPostgres_AnUnrelatedOutboxLockError_IsNotTranslatedIntoCapacityBusy()
    {
        await using var database = await databases.CreateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE FUNCTION platform.reject_policy_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."EventName" = 'platform.fact-capacity-policy-changed.v1' THEN
                        RAISE EXCEPTION USING ERRCODE = '55P03', MESSAGE = 'Controlled unrelated outbox failure.';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER reject_policy_insert BEFORE INSERT ON platform.outbox
                    FOR EACH ROW EXECUTE FUNCTION platform.reject_policy_insert();
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using var app = new PersistentIdentityApp(database.ConnectionString, "policy-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "policy-root-password");
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("platform");
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2, 16384, 16384, "operator-adjustment");
        var failure = await Assert.ThrowsAsync<PostgresException>(() => policies.AdjustAsync(request, "test-operator", DateTimeOffset.UtcNow, null));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, failure.SqlState);
        var after = await policies.ReadPolicyAsync();
        Assert.True(after.IsSuccess);
        Assert.Equal(1, after.Value.PolicyRevision);
        Assert.Equal(100000, after.Value.MaxRecords);
        Assert.Equal(0, after.Value.ControlCapacity.RetainedRecords);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
    }

    [PostgresFact]
    public async Task PlatformPostgres_PolicyReceipt_ReplaysOriginalAfterHostRestart()
    {
        await using var database = await databases.CreateAsync();
        var request = new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = "1",
            maxRecords = "1",
            maxPayloadBytes = "16384",
            maxRecordPayloadBytes = 16384,
            reason = "operator-adjustment",
        };
        string accepted;
        Guid eventId;
        await using (var app = new PersistentIdentityApp(database.ConnectionString, "policy-root-password", schedulingWorkerEnabled: false))
        {
            using var client = app.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "policy-root-password");
            using var change = await client.PutAsJsonAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
            var receipt = await change.Content.ReadApiDataAsync();
            Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());
            eventId = receipt.GetProperty("eventId").GetGuid();
            accepted = receipt.GetRawText();
        }
        await using (var restarted = new PersistentIdentityApp(database.ConnectionString, "policy-root-password", schedulingWorkerEnabled: false))
        {
            using var client = restarted.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "policy-root-password");
            using var repeated = await client.PutAsJsonAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(accepted, (await repeated.Content.ReadApiDataAsync()).GetRawText());
            using var read = await client.GetAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var snapshot = await read.Content.ReadApiDataAsync();
            Assert.Equal(1, snapshot.GetProperty("maxRecords").ReadHttpInt64());
            Assert.Equal(2, snapshot.GetProperty("policyRevision").ReadHttpInt64());
            Assert.Equal(1, snapshot.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
            await using var scope = restarted.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
            var pending = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
            Assert.Equal(eventId, pending.Id);
            Assert.Equal("platform.fact-capacity-policy-changed.v1", pending.EventName);
        }
    }

    [Fact]
    public async Task PlatformMemory_FullBusinessQuota_CanIncreasePolicyWithSeparateCommittedEvidence()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        using var first = await client.PutAsJsonAsync(new Uri("/api/platform/settings/policy.first", UriKind.Relative), new { value = "one" });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        using var refused = await client.PutAsJsonAsync(new Uri("/api/platform/settings/policy.second", UriKind.Relative), new { value = "two" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        var requestId = Guid.NewGuid();
        using var changed = await client.PutAsJsonAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative), new
        {
            requestId,
            expectedPolicyRevision = "1",
            maxRecords = "2",
            maxPayloadBytes = "16384",
            maxRecordPayloadBytes = 16384,
            reason = "operator-adjustment",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var receipt = await changed.Content.ReadApiDataAsync();
        Assert.Equal(requestId, receipt.GetProperty("requestId").GetGuid());
        Assert.True(receipt.GetProperty("changed").GetBoolean());
        Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());

        using var snapshotResponse = await client.GetAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
        var snapshot = await snapshotResponse.Content.ReadApiDataAsync();
        Assert.Equal(2, snapshot.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(1, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(1, snapshot.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());

        using var second = await client.PutAsJsonAsync(new Uri("/api/platform/settings/policy.second", UriKind.Relative), new { value = "two" });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var entries = await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        Assert.Equal(3, entries.Count);
        var control = Assert.Single(entries, entry => entry.EventName == "platform.fact-capacity-policy-changed.v1");
        using var payload = JsonDocument.Parse(control.Payload);
        Assert.Equal(requestId, payload.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal(1, payload.RootElement.GetProperty("previous").GetProperty("maxRecords").GetInt64());
        Assert.Equal(2, payload.RootElement.GetProperty("current").GetProperty("maxRecords").GetInt64());
        Assert.Equal(2, payload.RootElement.GetProperty("policyRevision").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("actorId").GetString()));
    }

    private static async Task AssertConditionalPolicyProtocolAsync(IServiceProvider services)
    {
        var policies = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("platform");
        var outbox = services.GetRequiredKeyedService<IOutboxStore>("platform");
        var initial = (await policies.ReadPolicyAsync()).Value;
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var requests = new[]
        {
            new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2, 16384, 16384, "operator-adjustment"),
            new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 3, 16384, 16384, "operator-adjustment"),
        };
        var results = await Task.WhenAll(requests.Select(request => Task.Run(() => policies.AdjustAsync(request, "test-operator", now, null))));
        var winningIndex = Assert.Single(Enumerable.Range(0, results.Length), index => results[index].IsSuccess);
        var winner = results[winningIndex].Value;
        Assert.Equal(SettingFactCapacityPolicyErrors.Conflict, Assert.Single(results, result => result.IsFailure).Error);
        Assert.Equal(2, winner.PolicyRevision);
        Assert.True(winner.Changed);
        Assert.NotNull(winner.EventId);
        Assert.NotEqual(winner.RequestId, winner.EventId.Value);
        var accepted = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, accepted.PolicyRevision);
        Assert.Equal(1, accepted.ControlCapacity.RetainedRecords);
        Assert.Equal(0, accepted.RetainedRecords);
        Assert.Equal(0, accepted.RetainedPayloadBytes);
        Assert.Equal(winner.EventId, Assert.Single(await outbox.ReadPendingAsync(10, now)).Id);
        var sameIdentityOtherContent = await policies.AdjustAsync(requests[winningIndex] with { MaxRecords = 999 }, "test-operator", now, null);
        Assert.True(sameIdentityOtherContent.IsFailure);
        Assert.Equal(SettingFactCapacityPolicyErrors.Conflict, sameIdentityOtherContent.Error);
        var sameIdentityOtherActor = await policies.AdjustAsync(requests[winningIndex], "another-operator", now, null);
        Assert.True(sameIdentityOtherActor.IsFailure);
        Assert.Equal(SettingFactCapacityPolicyErrors.Conflict, sameIdentityOtherActor.Error);
        Assert.Equal(accepted, (await policies.ReadPolicyAsync()).Value);
        var returnToInitial = new FactCapacityPolicyRequest(Guid.NewGuid(), 2, initial.MaxRecords,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
        var reverted = await policies.AdjustAsync(returnToInitial, "test-operator", now.AddMinutes(1), null);
        Assert.True(reverted.IsSuccess);
        Assert.Equal(3, reverted.Value.PolicyRevision);
        Assert.NotNull(reverted.Value.EventId);
        Assert.NotEqual(winner.EventId, reverted.Value.EventId);
        var staleAba = await policies.AdjustAsync(requests[winningIndex] with { RequestId = Guid.NewGuid() }, "test-operator", now, null);
        Assert.True(staleAba.IsFailure);
        Assert.Equal(SettingFactCapacityPolicyErrors.Conflict, staleAba.Error);
        // Replay is a past decision, even though the current policy has advanced to revision three.
        Assert.Equal(winner, (await policies.AdjustAsync(requests[winningIndex], "test-operator", now.AddDays(1), null)).Value);
        var noOpRequest = returnToInitial with { RequestId = Guid.NewGuid(), ExpectedPolicyRevision = 3 };
        var noOp = await policies.AdjustAsync(noOpRequest, "test-operator", now.AddDays(1), null);
        Assert.True(noOp.IsSuccess);
        Assert.False(noOp.Value.Changed);
        Assert.Null(noOp.Value.EventId);
        Assert.Equal(3, noOp.Value.PolicyRevision);
        var final = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(3, final.PolicyRevision);
        Assert.Equal(initial.MaxRecords, final.MaxRecords);
        Assert.Equal(3, final.ControlCapacity.RetainedRecords);
        Assert.Equal(0, final.RetainedRecords);
        Assert.Equal(0, final.RetainedPayloadBytes);
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, now.AddDays(1))).Count);
        Assert.Equal(noOp.Value, (await policies.AdjustAsync(noOpRequest, "test-operator", now.AddDays(2), null)).Value);
        Assert.Equal(final, (await policies.ReadPolicyAsync()).Value);
    }

    private sealed class PolicyApp : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:AuditDelivery:MemoryCapacity:MaxRecords"] = "1",
                ["Platform:AuditDelivery:MemoryCapacity:MaxPayloadBytes"] = "16384",
                ["Platform:AuditDelivery:MemoryCapacity:MaxRecordPayloadBytes"] = "16384",
                ["Platform:AuditDelivery:Cleanup:Enabled"] = "false",
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
