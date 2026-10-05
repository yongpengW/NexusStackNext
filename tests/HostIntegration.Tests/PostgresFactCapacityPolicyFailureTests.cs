using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Endpoints;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Files.Endpoints;
using NexusStackNext.Identity.Endpoints;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Endpoints;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.Scheduling.Endpoints;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PostgresFactCapacityPolicyFailureTests
{
    [PostgresFact]
    public Task PlatformPostgres_ReceiptWriteFailure_RollsBackAllPolicyEvidenceAndAllowsOriginalRetry()
        => VerifyReceiptFailureAsync("platform", "Platform");

    [PostgresFact]
    public Task IdentityPostgres_ReceiptWriteFailure_RollsBackAllPolicyEvidenceAndAllowsOriginalRetry()
        => VerifyReceiptFailureAsync("identity", "Identity");
    [PostgresFact]
    public Task FilesPostgres_ReceiptWriteFailure_RollsBackAllPolicyEvidenceAndAllowsOriginalRetry()
        => VerifyReceiptFailureAsync("files", "Files");
    [PostgresFact]
    public Task SchedulingPostgres_ReceiptWriteFailure_RollsBackAllPolicyEvidenceAndAllowsOriginalRetry()
        => VerifyReceiptFailureAsync("scheduling", "Scheduling");
    [PostgresFact]
    public Task CostingPostgres_ReceiptWriteFailure_RollsBackAllPolicyEvidenceAndAllowsOriginalRetry()
        => VerifyReceiptFailureAsync("costing", "Costing");
    [PostgresFact]
    public Task PricingPostgres_ReceiptWriteFailure_RollsBackAllPolicyEvidenceAndAllowsOriginalRetry()
        => VerifyReceiptFailureAsync("pricing", "Pricing");

    [PostgresFact]
    public Task PlatformPostgres_CallerCancelsActualLockWait_NoPartialCommitAndOriginalRequestCanRetry()
        => VerifyWaitingCancellationAsync("platform", "Platform");

    [PostgresFact]
    public Task IdentityPostgres_CallerCancelsActualLockWait_NoPartialCommitAndOriginalRequestCanRetry()
        => VerifyWaitingCancellationAsync("identity", "Identity");
    [PostgresFact]
    public Task FilesPostgres_CallerCancelsActualLockWait_NoPartialCommitAndOriginalRequestCanRetry()
        => VerifyWaitingCancellationAsync("files", "Files");
    [PostgresFact]
    public Task SchedulingPostgres_CallerCancelsActualLockWait_NoPartialCommitAndOriginalRequestCanRetry()
        => VerifyWaitingCancellationAsync("scheduling", "Scheduling");
    [PostgresFact]
    public Task CostingPostgres_CallerCancelsActualLockWait_NoPartialCommitAndOriginalRequestCanRetry()
        => VerifyWaitingCancellationAsync("costing", "Costing");
    [PostgresFact]
    public Task PricingPostgres_CallerCancelsActualLockWait_NoPartialCommitAndOriginalRequestCanRetry()
        => VerifyWaitingCancellationAsync("pricing", "Pricing");

    [PostgresFact]
    public Task PlatformPostgres_CallerCancelsBeforeReceiptCommit_RollsBackPreparedFactAndAllowsOriginalRetry()
        => VerifyWaitingCancellationAsync("platform", "Platform", beforeReceipt: true);

    [PostgresFact]
    public Task IdentityPostgres_CallerCancelsBeforeReceiptCommit_RollsBackPreparedFactAndAllowsOriginalRetry()
        => VerifyWaitingCancellationAsync("identity", "Identity", beforeReceipt: true);
    [PostgresFact]
    public Task FilesPostgres_CallerCancelsBeforeReceiptCommit_RollsBackPreparedFactAndAllowsOriginalRetry()
        => VerifyWaitingCancellationAsync("files", "Files", beforeReceipt: true);
    [PostgresFact]
    public Task SchedulingPostgres_CallerCancelsBeforeReceiptCommit_RollsBackPreparedFactAndAllowsOriginalRetry()
        => VerifyWaitingCancellationAsync("scheduling", "Scheduling", beforeReceipt: true);
    [PostgresFact]
    public Task CostingPostgres_CallerCancelsBeforeReceiptCommit_RollsBackPreparedFactAndAllowsOriginalRetry()
        => VerifyWaitingCancellationAsync("costing", "Costing", beforeReceipt: true);
    [PostgresFact]
    public Task PricingPostgres_CallerCancelsBeforeReceiptCommit_RollsBackPreparedFactAndAllowsOriginalRetry()
        => VerifyWaitingCancellationAsync("pricing", "Pricing", beforeReceipt: true);

    private static async Task VerifyWaitingCancellationAsync(string context, string configurationContext, bool beforeReceipt = false)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await MigrateAsync(database, configurationContext);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        const string applicationName = "nsn-policy-waiting-caller";
        var moduleConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = applicationName }.ConnectionString;
        await using var host = CreateModule(configurationContext, moduleConnection, now);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
            var outbox = context is "costing" or "pricing" ? scope.ServiceProvider.GetRequiredService<IOutboxStore>()
                : scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
            var before = (await policies.ReadPolicyAsync()).Value;
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000014"), 1,
                before.MaxRecords + 1, before.MaxPayloadBytes, before.MaxRecordPayloadBytes, "operator-adjustment");
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            if (beforeReceipt)
            {
                await using var arrange = new NpgsqlCommand($"""
                    CREATE FUNCTION {context}.wait_policy_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN PERFORM pg_advisory_xact_lock(101013); RETURN NEW; END $$;
                    CREATE TRIGGER wait_policy_receipt BEFORE INSERT ON {context}.fact_policy_receipts
                        FOR EACH ROW EXECUTE FUNCTION {context}.wait_policy_receipt();
                    """, connection);
                await arrange.ExecuteNonQueryAsync();
            }
            await using var hold = await connection.BeginTransactionAsync();
            await using (var acquire = new NpgsqlCommand(beforeReceipt
                ? "SELECT 1 FROM (SELECT pg_advisory_xact_lock(101013)) AS held"
                : $"SELECT \"Id\" FROM {context}.fact_policy_control WHERE \"Id\" = 1 FOR UPDATE", connection, hold))
            {
                Assert.Equal(1, await acquire.ExecuteScalarAsync());
            }
            using var caller = new CancellationTokenSource();
            var adjusting = policies.AdjustAsync(request, "test-operator", now, null, caller.Token);
            try
            {
                using var observe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var waiting = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                        WHERE datname = @database AND application_name = @application
                            AND wait_event_type = 'Lock' AND query LIKE @target
                            AND (NOT @receipt_wait OR wait_event = 'advisory'))
                    """, connection, hold);
                waiting.Parameters.AddWithValue("database", connection.Database);
                waiting.Parameters.AddWithValue("application", applicationName);
                waiting.Parameters.AddWithValue("target", beforeReceipt ? "%fact_policy_receipts%" : "%fact_policy_control%");
                waiting.Parameters.AddWithValue("receipt_wait", beforeReceipt);
                await using var refresh = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", connection, hold);
                while (true)
                {
                    await refresh.ExecuteNonQueryAsync(observe.Token);
                    if ((bool)(await waiting.ExecuteScalarAsync(observe.Token))!) { break; }
                    await Task.Delay(10, observe.Token);
                }
                Assert.False(adjusting.IsCompleted);
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adjusting.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(adjusting.IsCompleted);
                // The lock is still held: cancellation must finish the actual write, rather than abandon it.
                Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
                Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            }
            finally
            {
                caller.Cancel();
                await hold.RollbackAsync();
                try { await adjusting; }
                catch (OperationCanceledException) { }
                if (beforeReceipt)
                {
                    await using var repair = new NpgsqlCommand($"""
                        DROP TRIGGER wait_policy_receipt ON {context}.fact_policy_receipts;
                        DROP FUNCTION {context}.wait_policy_receipt();
                        """, connection);
                    await repair.ExecuteNonQueryAsync();
                }
            }
            Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            var retry = await policies.AdjustAsync(request, "test-operator", now, null);
            Assert.True(retry.IsSuccess);
            Assert.True(retry.Value.Changed);
            Assert.Equal(2, retry.Value.PolicyRevision);
            var after = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(2, after.PolicyRevision);
            Assert.Equal(before.MaxRecords + 1, after.MaxRecords);
            Assert.Equal(before.RetainedRecords, after.RetainedRecords);
            Assert.Equal(before.RetainedPayloadBytes, after.RetainedPayloadBytes);
            Assert.Equal(1, after.ControlCapacity.RetainedRecords);
            var fact = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            Assert.Equal(retry.Value.EventId, fact.Id);
            Assert.Equal(retry.Value, (await policies.AdjustAsync(request, "test-operator", now.AddMinutes(1), null)).Value);
            Assert.Equal(after, (await policies.ReadPolicyAsync()).Value);
            Assert.Equal(fact, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)));
        }
        finally { await host.StopAsync(); }
    }

    private static async Task VerifyReceiptFailureAsync(string context, string configurationContext)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await MigrateAsync(database, configurationContext);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        await using var host = CreateModule(configurationContext, database.ConnectionString, now);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
            var outbox = context is "costing" or "pricing" ? scope.ServiceProvider.GetRequiredService<IOutboxStore>()
                : scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
            var before = (await policies.ReadPolicyAsync()).Value;
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000013"), 1,
                before.MaxRecords + 1, before.MaxPayloadBytes, before.MaxRecordPayloadBytes, "operator-adjustment");
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using (var arrange = new NpgsqlCommand($"""
                CREATE FUNCTION {context}.fail_policy_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Controlled policy receipt failure'; END $$;
                CREATE TRIGGER fail_policy_receipt BEFORE INSERT ON {context}.fact_policy_receipts
                    FOR EACH ROW EXECUTE FUNCTION {context}.fail_policy_receipt();
                """, connection))
            {
                await arrange.ExecuteNonQueryAsync();
            }
            var failure = await Assert.ThrowsAsync<PostgresException>(() => policies.AdjustAsync(request, "test-operator", now, null));
            Assert.Equal(PostgresErrorCodes.RaiseException, failure.SqlState);
            Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            await using (var repair = new NpgsqlCommand($"""
                DROP TRIGGER fail_policy_receipt ON {context}.fact_policy_receipts;
                DROP FUNCTION {context}.fail_policy_receipt();
                """, connection))
            {
                await repair.ExecuteNonQueryAsync();
            }
            var retry = await policies.AdjustAsync(request, "test-operator", now, null);
            Assert.True(retry.IsSuccess);
            Assert.True(retry.Value.Changed);
            Assert.Equal(2, retry.Value.PolicyRevision);
            var after = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(2, after.PolicyRevision);
            Assert.Equal(before.MaxRecords + 1, after.MaxRecords);
            Assert.Equal(before.RetainedRecords, after.RetainedRecords);
            Assert.Equal(before.RetainedPayloadBytes, after.RetainedPayloadBytes);
            Assert.Equal(1, after.ControlCapacity.RetainedRecords);
            Assert.True(after.ControlCapacity.RetainedPayloadBytes > 0);
            var fact = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            Assert.Equal(retry.Value.EventId, fact.Id);
            Assert.Equal(retry.Value, (await policies.AdjustAsync(request, "test-operator", now.AddMinutes(1), null)).Value);
            Assert.Equal(after, (await policies.ReadPolicyAsync()).Value);
            Assert.Equal(fact, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)));
        }
        finally { await host.StopAsync(); }
    }

    private static WebApplication CreateModule(string context, string connection, DateTimeOffset now)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "policy-failure-test-signing-key-long-enough-for-hs256",
            [$"{context}:Storage:Provider"] = "Postgres",
            [$"ConnectionStrings:{context}"] = connection,
            [$"{context}:Messaging:Enabled"] = "false",
            [$"{context}:Worker:Enabled"] = "false",
            [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
            [$"{context}:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            [$"{context}:AuditDelivery:CapacityRead:Timeout"] = "00:00:30",
            ["Files:StorageRoot"] = Path.Combine(Path.GetTempPath(), "nsn-policy-failure-" + Guid.NewGuid().ToString("N")),
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(new FixedClock(now));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        switch (context)
        {
            case "Platform": builder.Services.AddPlatformModule(builder.Configuration, builder.Environment); break;
            case "Identity": builder.Services.AddIdentityModule(builder.Configuration, builder.Environment); break;
            case "Files": builder.Services.AddFilesModule(builder.Configuration, builder.Environment); break;
            case "Scheduling": builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment); break;
            case "Costing": builder.Services.AddCostingModule(builder.Configuration); break;
            case "Pricing": builder.Services.AddPricingModule(builder.Configuration); break;
            default: throw new ArgumentException("Unknown test module.", nameof(context));
        }
        return builder.Build();
    }

    private static Task MigrateAsync(IdentityJourneyDatabase database, string context) => context switch
    {
        "Costing" => CostingDatabase.MigrateAsync(database.ConnectionString),
        "Pricing" => PricingDatabase.MigrateAsync(database.ConnectionString),
        _ => database.MigrateAsync(),
    };
}
