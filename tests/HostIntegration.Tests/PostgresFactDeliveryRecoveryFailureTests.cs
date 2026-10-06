using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Endpoints;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(PlatformJourneyDefinition.Name)]
public sealed class PostgresFactDeliveryRecoveryFailureTests(PlatformJourneyTemplate databases)
{
    [PostgresFact]
    public async Task ReceiptAndLedgerWriteFailures_PreserveTheirRealMeaning_AndLeaveNoPartialRecovery()
    {
        await using var database = await databases.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        await using var host = CreateModule(database.ConnectionString, now);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
            var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
            var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
            Assert.True((await settings.WriteAsync(SettingKey.Create("recovery.write-faults").Value, "retained")).IsSuccess);
            var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-stop", now, 0));
            var stopped = (await delivery.GetAsync(original.Id)).Value;
            var empty = (await delivery.ReadRecoveryCapacityAsync()).Value;
            var businessBefore = (await business.ReadAsync()).Value;
            var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, now, 0, "dependency-restored");
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            foreach (var fault in FactRecoveryWriteFault.Cases)
            {
                await using var installed = await fault.InstallAsync(connection, "platform");
                var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                    delivery.RecoverAsync(request, "recovery-operator", now, null));
                Assert.Equal(fault.State, failure.SqlState);
                Assert.Equal(fault.Constraint, failure.ConstraintName);
                Assert.Equal(stopped, (await delivery.GetAsync(original.Id)).Value);
                Assert.Equal(empty, (await delivery.ReadRecoveryCapacityAsync()).Value);
                Assert.Equal(businessBefore, (await business.ReadAsync()).Value);
                Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
                Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
            }
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    delivery.RecoverAsync(request, "recovery-operator", now, null, cancelled.Token));
            }
            Assert.Equal(stopped, (await delivery.GetAsync(original.Id)).Value);
            Assert.Equal(empty, (await delivery.ReadRecoveryCapacityAsync()).Value);
            var accepted = await delivery.RecoverAsync(request, "recovery-operator", now, null);
            Assert.True(accepted.IsSuccess);
            Assert.Equal(1, accepted.Value.RetryRevision);
            Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
            var pending = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            Assert.Equal(original.Id, pending.Id);
            Assert.Equal(original.Payload, pending.Payload);
            Assert.Equal(original.OccurredAt, pending.OccurredAt);
            Assert.Equal(businessBefore, (await business.ReadAsync()).Value);
            Assert.Equal(1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        }
        finally { await host.StopAsync(); }
    }

    [PostgresFact]
    public async Task CleanupLedgerWriteFailure_RollsBackRemovedReceiptsAndCapacity_WithoutChangingOriginalFacts()
    {
        await using var database = await databases.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        await using var host = CreateModule(database.ConnectionString, now);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
            var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
            var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
            Assert.True((await settings.WriteAsync(SettingKey.Create("cleanup.rollback").Value, "retained")).IsSuccess);
            var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
            Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
            var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, now, 0, "manual-retry");
            var accepted = await delivery.RecoverAsync(request, "cleanup-operator", now, null);
            Assert.True(accepted.IsSuccess);
            var beforeCapacity = (await delivery.ReadRecoveryCapacityAsync()).Value;
            var beforeBusiness = (await business.ReadAsync()).Value;
            var beforeFact = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using (var fault = new NpgsqlCommand("""
                CREATE FUNCTION platform.reject_recovery_release() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."RetainedRecords" < OLD."RetainedRecords" THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'controlled_recovery_release_failure',
                            MESSAGE = 'Controlled recovery release failure';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER reject_recovery_release BEFORE UPDATE ON platform.fact_recovery_control
                    FOR EACH ROW EXECUTE FUNCTION platform.reject_recovery_release();
                """, connection))
            { await fault.ExecuteNonQueryAsync(); }
            var failure = await Assert.ThrowsAsync<PostgresException>(() => delivery.CleanupRecoveriesAsync(1, now.AddDays(7)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal("controlled_recovery_release_failure", failure.ConstraintName);
            Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
            Assert.Equal(beforeCapacity, (await delivery.ReadRecoveryCapacityAsync()).Value);
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
            Assert.Equal(beforeFact, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)));
            await using (var repair = new NpgsqlCommand("""
                DROP TRIGGER reject_recovery_release ON platform.fact_recovery_control;
                DROP FUNCTION platform.reject_recovery_release();
                """, connection))
            { await repair.ExecuteNonQueryAsync(); }
            Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7)));
            Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
            var released = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
            Assert.Equal(0, released.RetainedRecords);
            Assert.Equal(0, released.RetainedPayloadBytes);
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
            Assert.Equal(beforeFact, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)));
        }
        finally { await host.StopAsync(); }
    }

    [PostgresFact]
    public async Task CallerCancelsBeforeReceiptCommit_RollsBackPreparedRecovery_AndOriginalRequestCanRetry()
    {
        await using var database = await databases.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        const string applicationName = "nsn-recovery-waiting-caller";
        var moduleConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        { ApplicationName = applicationName }.ConnectionString;
        await using var host = CreateModule(moduleConnection, now);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
            var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
            var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
            Assert.True((await settings.WriteAsync(SettingKey.Create("recovery.cancel").Value, "retained")).IsSuccess);
            var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
            Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
            var stoppedBefore = Assert.Single(await delivery.ListAsync("DeadLettered", 10));
            var beforeCapacity = (await delivery.ReadRecoveryCapacityAsync()).Value;
            var beforeBusiness = (await business.ReadAsync()).Value;
            var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, now, 0, "dependency-restored");
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using (var arrange = new NpgsqlCommand("""
                CREATE FUNCTION platform.wait_recovery_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_advisory_xact_lock(103013); RETURN NEW; END $$;
                CREATE TRIGGER wait_recovery_receipt BEFORE INSERT ON platform.fact_recovery_receipts
                    FOR EACH ROW EXECUTE FUNCTION platform.wait_recovery_receipt();
                """, connection))
            { await arrange.ExecuteNonQueryAsync(); }
            await using var hold = await connection.BeginTransactionAsync();
            await using (var acquire = new NpgsqlCommand("SELECT 1 FROM (SELECT pg_advisory_xact_lock(103013)) AS held", connection, hold))
            { Assert.Equal(1, await acquire.ExecuteScalarAsync()); }
            using var caller = new CancellationTokenSource();
            var recovering = delivery.RecoverAsync(request, "recovery-operator", now, null, caller.Token);
            try
            {
                using var observe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var waiting = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                        WHERE datname = @database AND application_name = @application
                            AND wait_event_type = 'Lock' AND wait_event = 'advisory'
                            AND query LIKE '%fact_recovery_receipts%')
                    """, connection, hold);
                waiting.Parameters.AddWithValue("database", connection.Database);
                waiting.Parameters.AddWithValue("application", applicationName);
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
                // The external lock is still held; cancellation must finish the real write rather than abandon it.
                Assert.Equal(stoppedBefore, Assert.Single(await delivery.ListAsync("DeadLettered", 10)));
                Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
                Assert.Equal(beforeCapacity, (await delivery.ReadRecoveryCapacityAsync()).Value);
                Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
                Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
            }
            finally
            {
                caller.Cancel();
                await hold.RollbackAsync();
                try { await recovering; }
                catch (OperationCanceledException) { }
                await using var repair = new NpgsqlCommand("""
                    DROP TRIGGER wait_recovery_receipt ON platform.fact_recovery_receipts;
                    DROP FUNCTION platform.wait_recovery_receipt();
                    """, connection);
                await repair.ExecuteNonQueryAsync();
            }
            var accepted = await delivery.RecoverAsync(request, "recovery-operator", now, null);
            Assert.True(accepted.IsSuccess);
            Assert.Equal(1, accepted.Value.RetryRevision);
            Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
            var pending = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            Assert.Equal(original.Id, pending.Id);
            Assert.Equal(original.Payload, pending.Payload);
            Assert.Equal(original.OccurredAt, pending.OccurredAt);
            Assert.Equal(1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
        }
        finally { await host.StopAsync(); }
    }

    [PostgresFact]
    public async Task OwnedRecoveryLedgerContention_ReturnsBusyWithoutChangingTheStoppedFact_AndAllowsOriginalRetry()
    {
        await using var database = await databases.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        await using var host = CreateModule(database.ConnectionString, now);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
            var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
            Assert.True((await settings.WriteAsync(SettingKey.Create("recovery.busy").Value, "retained")).IsSuccess);
            var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
            Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
            var stoppedBefore = Assert.Single(await delivery.ListAsync("DeadLettered", 10));
            var beforeCapacity = (await delivery.ReadRecoveryCapacityAsync()).Value;
            var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, now, 0, "manual-retry");
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using (var hold = await connection.BeginTransactionAsync())
            {
                await using var acquire = new NpgsqlCommand(
                    "SELECT \"Id\" FROM platform.fact_recovery_control WHERE \"Id\" = 1 FOR UPDATE", connection, hold);
                Assert.Equal(1, await acquire.ExecuteScalarAsync());
                var rejected = await delivery.RecoverAsync(request, "recovery-operator", now, null).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(rejected.IsFailure);
                Assert.Equal("audit_capacity.busy", rejected.Error.Code);
                Assert.Equal(stoppedBefore, Assert.Single(await delivery.ListAsync("DeadLettered", 10)));
                Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
                Assert.Equal(beforeCapacity, (await delivery.ReadRecoveryCapacityAsync()).Value);
                Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
                await hold.RollbackAsync();
            }
            var accepted = await delivery.RecoverAsync(request, "recovery-operator", now, null);
            Assert.True(accepted.IsSuccess);
            Assert.Equal(1, accepted.Value.RetryRevision);
            Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
            Assert.Equal(1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        }
        finally { await host.StopAsync(); }
    }

    private static WebApplication CreateModule(string connection, DateTimeOffset now)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Storage:Provider"] = "Postgres",
            ["ConnectionStrings:Platform"] = connection,
            ["Platform:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Platform:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Platform:AuditDelivery:RecoveryMaintenance:Enabled"] = "false",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(new FixedClock(now));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddPlatformModule(builder.Configuration, builder.Environment);
        return builder.Build();
    }
}
