using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactRecoveryPersistenceFailureTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public Task PlatformPostgres_ReceiptAndLedgerFailuresPreserveMeaning_AndCancelBeforeCommitLeavesNoPartialRecovery()
        => VerifyPlatformAsync("platform");

    [PostgresFact]
    public Task IdentityPostgres_ReceiptAndLedgerFailuresPreserveMeaning_AndCancelBeforeCommitLeavesNoPartialRecovery()
        => VerifyPlatformAsync("identity");

    [PostgresFact]
    public Task FilesPostgres_ReceiptAndLedgerFailuresPreserveMeaning_AndCancelBeforeCommitLeavesNoPartialRecovery()
        => VerifyPlatformAsync("files");

    [PostgresFact]
    public Task SchedulingPostgres_ReceiptAndLedgerFailuresPreserveMeaning_AndCancelBeforeCommitLeavesNoPartialRecovery()
        => VerifyPlatformAsync("scheduling");

    [PostgresFact]
    public Task CostingPostgres_ReceiptAndLedgerFailuresPreserveMeaning_AndCancelBeforeCommitLeavesNoPartialRecovery()
        => VerifyBusinessAsync("costing");

    [PostgresFact]
    public Task PricingPostgres_ReceiptAndLedgerFailuresPreserveMeaning_AndCancelBeforeCommitLeavesNoPartialRecovery()
        => VerifyBusinessAsync("pricing");

    private async Task VerifyPlatformAsync(string source)
    {
        await using var database = await databases.CreateAsync();
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        { ApplicationName = "nsn-fact-recovery-cancel" }.ConnectionString;
        await using var app = new PersistentIdentityApp(connection, PlatformAppWithRootAccount.RootPassword, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, connection, source);
    }

    private async Task VerifyBusinessAsync(string source)
    {
        await using var database = await databases.CreateAsync(source);
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        { ApplicationName = "nsn-fact-recovery-cancel" }.ConnectionString;
        var assembly = source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
        await using var host = await BusinessProcess.StartAsync(assembly, source == "costing" ? "Costing" : "Pricing", connection);
        host.Authenticate();
        await using var reader = source == "costing" ? TaskOperationTests.CreateCostingApp(connection, null)
            : TaskOperationTests.CreatePricingApp(connection, null);
        await VerifyAsync(host.Client, reader.Services, connection, source);
    }

    private static async Task VerifyAsync(HttpClient client, IServiceProvider services, string connectionString, string source)
    {
        Assert.Contains(source, new[] { "platform", "identity", "files", "scheduling", "costing", "pricing" });
        var policyPath = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        using var initial = await client.GetAsync(policyPath);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var policy = await initial.Content.ReadApiDataAsync();
        using var changed = await client.PutAsJsonAsync(policyPath, new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = policy.GetProperty("policyRevision").GetString(),
            maxRecords = policy.GetProperty("maxRecords").GetString(),
            maxPayloadBytes = policy.GetProperty("maxPayloadBytes").GetString(),
            maxRecordPayloadBytes = policy.GetProperty("maxRecordPayloadBytes").GetInt32() + 1,
            reason = "operator-adjustment",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var messageId = (await changed.Content.ReadApiDataAsync()).GetProperty("eventId").GetGuid();
        using var beforeResponse = await client.GetAsync(policyPath);
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
        var businessBefore = await beforeResponse.Content.ReadApiDataAsync();
        await using var scope = services.CreateAsyncScope();
        var outbox = FactRecoveryProtocolTests.GetOutbox(scope.ServiceProvider, source);
        var delivery = FactRecoveryProtocolTests.GetPort(scope.ServiceProvider, source);
        var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.True(await outbox.MarkDeadLetteredAsync(messageId, "controlled-persistence-stop", now, 0));
        var stopped = (await delivery.GetAsync(messageId)).Value;
        var empty = (await delivery.ReadRecoveryCapacityAsync()).Value;
        var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), messageId, now, 0, "dependency-restored");
        foreach (var invalidTime in new[] { DateTimeOffset.MaxValue,
            DateTimeOffset.MaxValue.AddDays(-7).ToOffset(TimeSpan.FromHours(14)),
            new DateTimeOffset(DateTime.MaxValue.AddDays(-7), TimeSpan.FromHours(-14)) })
        {
            Assert.Equal($"{source}.delivery_recovery.invalid",
                (await delivery.RecoverAsync(request, "original-operator", invalidTime, null)).Error.Code);
            await AssertUnchangedAsync();
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var fault in FactRecoveryWriteFault.Cases)
        {
            await using var installed = await fault.InstallAsync(connection, source);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => delivery.RecoverAsync(request, "original-operator", now, null));
            Assert.Equal(fault.State, failure.SqlState);
            Assert.Equal(fault.Constraint, failure.ConstraintName);
            await AssertUnchangedAsync();
        }

        await using (var arrange = new NpgsqlCommand($"""
            CREATE FUNCTION {source}.wait_recovery_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(103063); RETURN NEW; END $$;
            CREATE TRIGGER wait_recovery_receipt BEFORE INSERT ON {source}.fact_recovery_receipts
                FOR EACH ROW EXECUTE FUNCTION {source}.wait_recovery_receipt();
            """, connection))
        { await arrange.ExecuteNonQueryAsync(); }
        await using var hold = await connection.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT 1 FROM (SELECT pg_advisory_xact_lock(103063)) AS held", connection, hold))
        { Assert.Equal(1, await acquire.ExecuteScalarAsync()); }
        using var caller = new CancellationTokenSource();
        var recovering = delivery.RecoverAsync(request, "original-operator", now, null, caller.Token);
        try
        {
            using var observe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var waiting = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = @database
                    AND application_name = 'nsn-fact-recovery-cancel' AND wait_event_type = 'Lock'
                    AND wait_event = 'advisory' AND query LIKE '%fact_recovery_receipts%')
                """, connection, hold);
            waiting.Parameters.AddWithValue("database", connection.Database);
            await using var refresh = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", connection, hold);
            while (true)
            {
                await refresh.ExecuteNonQueryAsync(observe.Token);
                if ((bool)(await waiting.ExecuteScalarAsync(observe.Token))!) { break; }
                await Task.Delay(10, observe.Token);
            }
            Assert.False(recovering.IsCompleted);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovering.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(recovering.IsCompleted);
            await AssertUnchangedAsync();
        }
        finally
        {
            caller.Cancel();
            await hold.RollbackAsync();
            try { await recovering; }
            catch (OperationCanceledException) { }
            await using var repair = new NpgsqlCommand($"""
                DROP TRIGGER wait_recovery_receipt ON {source}.fact_recovery_receipts;
                DROP FUNCTION {source}.wait_recovery_receipt();
                """, connection);
            await repair.ExecuteNonQueryAsync();
        }
        var accepted = await delivery.RecoverAsync(request, "original-operator", now, null);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(1, accepted.Value.RetryRevision);
        Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        Assert.Equal(original.Id, pending.Id);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        using var afterResponse = await client.GetAsync(policyPath);
        Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
        Assert.True(JsonElement.DeepEquals(businessBefore, await afterResponse.Content.ReadApiDataAsync()));

        Assert.True(await outbox.MarkDeadLetteredAsync(messageId, "controlled-maximum-revision", now, 1));
        await using (var maximum = new NpgsqlCommand($"""
            UPDATE {source}.outbox SET "RetryRevision" = @maximum WHERE "Id" = @id
            """, connection))
        {
            maximum.Parameters.AddWithValue("maximum", long.MaxValue);
            maximum.Parameters.AddWithValue("id", messageId);
            Assert.Equal(1, await maximum.ExecuteNonQueryAsync());
        }
        var atMaximum = (await delivery.GetAsync(messageId)).Value;
        Assert.Equal(long.MaxValue, atMaximum.RetryRevision);
        Assert.Equal("DeadLettered", atMaximum.State);
        var capacityBeforeOverflow = (await delivery.ReadRecoveryCapacityAsync()).Value;
        var overflowRequest = new FactDeliveryRecoveryRequest(Guid.NewGuid(), messageId, now, long.MaxValue, "manual-retry");
        Assert.Equal($"{source}.delivery_conflict",
            (await delivery.RecoverAsync(overflowRequest, "original-operator", now, null)).Error.Code);
        Assert.Equal(atMaximum, (await delivery.GetAsync(messageId)).Value);
        Assert.Equal(capacityBeforeOverflow, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(overflowRequest.RequestId)).Error.Code);
        Assert.Equal(accepted.Value, (await delivery.RecoverAsync(request, "original-operator", now, null)).Value);
        Assert.Equal(atMaximum, (await delivery.GetAsync(messageId)).Value);
        await outbox.MarkDeliveredAsync(messageId, now.AddSeconds(1));
        Assert.Equal("Delivered", (await delivery.GetAsync(messageId)).Value.State);
        Assert.False(await outbox.MarkDeadLetteredAsync(messageId, "late-maximum-stop", now, long.MaxValue));

        async Task AssertUnchangedAsync()
        {
            Assert.Equal(stopped, (await delivery.GetAsync(messageId)).Value);
            Assert.Equal(empty, (await delivery.ReadRecoveryCapacityAsync()).Value);
            Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
            Assert.DoesNotContain(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        }
    }
}
