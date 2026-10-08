using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
public sealed class FactRecoveryRollbackConcurrencyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public Task Platform_ConcurrentAcceptedRecoveryPreventsDestructiveRollback() => VerifyAsync("platform");

    [PostgresFact]
    public Task Identity_ConcurrentAcceptedRecoveryPreventsDestructiveRollback() => VerifyAsync("identity");

    [PostgresFact]
    public Task Files_ConcurrentAcceptedRecoveryPreventsDestructiveRollback() => VerifyAsync("files");

    [PostgresFact]
    public Task Scheduling_ConcurrentAcceptedRecoveryPreventsDestructiveRollback() => VerifyAsync("scheduling");

    [PostgresFact]
    public Task Costing_ConcurrentAcceptedRecoveryPreventsDestructiveRollback() => VerifyAsync("costing");

    [PostgresFact]
    public Task Pricing_ConcurrentAcceptedRecoveryPreventsDestructiveRollback() => VerifyAsync("pricing");

    private async Task VerifyAsync(string source)
    {
        await using var database = await databases.CreateAsync(source is "costing" or "pricing" ? source + "-only" : "platform");
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        { ApplicationName = "nsn-recovery-rollback-race" }.ConnectionString;
        await using var costing = source == "costing" ? TaskOperationTests.CreateCostingApp(connectionString, null) : null;
        await using var pricing = source == "pricing" ? TaskOperationTests.CreatePricingApp(connectionString, null) : null;
        await using var platform = source is "costing" or "pricing" ? null
            : new PersistentIdentityApp(connectionString, schedulingWorkerEnabled: false);
        var services = costing?.Services ?? pricing?.Services ?? platform!.Services;
        await using var scope = services.CreateAsyncScope();
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        Guid? eventId;
        await using var businessHost = source is "costing" or "pricing"
            ? await BusinessProcess.StartAsync(source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location,
                source == "costing" ? "Costing" : "Pricing", connectionString) : null;
        if (businessHost is not null)
        {
            businessHost.Authenticate();
            eventId = (await FactCapacityPolicyBrokerJourneyTests.AdjustAsync(businessHost.Client, source)).EventId;
        }
        else
        {
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source);
            var before = (await policies.ReadPolicyAsync()).Value;
            var adjusted = await policies.AdjustAsync(new(Guid.NewGuid(), before.PolicyRevision, before.MaxRecords + 1,
                before.MaxPayloadBytes, before.MaxRecordPayloadBytes, "operator-adjustment"), "policy-operator", now, null);
            Assert.True(adjusted.IsSuccess);
            eventId = adjusted.Value.EventId;
        }
        var outbox = FactRecoveryProtocolTests.GetOutbox(scope.ServiceProvider, source);
        var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == eventId);
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-migration-race", now, 0));
        var delivery = FactRecoveryProtocolTests.GetPort(scope.ServiceProvider, source);
        var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, now, 0, "dependency-restored");
        Migration migration = source switch
        {
            "platform" => new NexusStackNext.Platform.Infrastructure.Persistence.Migrations.ConditionalFactRecovery(),
            "identity" => new NexusStackNext.Identity.Infrastructure.Persistence.Migrations.ConditionalFactRecovery(),
            "files" => new NexusStackNext.Files.Infrastructure.Persistence.Migrations.ConditionalFactRecovery(),
            "scheduling" => new NexusStackNext.Scheduling.Infrastructure.Persistence.Migrations.ConditionalFactRecovery(),
            "costing" => new NexusStackNext.Costing.Infrastructure.Migrations.ConditionalFactRecovery(),
            "pricing" => new NexusStackNext.Pricing.Infrastructure.Migrations.ConditionalFactRecovery(),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        var contextType = migration.GetType().GetCustomAttribute<DbContextAttribute>()?.ContextType;
        Assert.NotNull(contextType);
        var context = Assert.IsAssignableFrom<DbContext>(scope.ServiceProvider.GetRequiredService(contextType));
        var migrations = context.Database.GetMigrations().ToArray();
        var recoveryIndex = Array.FindIndex(migrations, name => name.EndsWith("_ConditionalFactRecovery", StringComparison.Ordinal));
        Assert.True(recoveryIndex > 0, "恢复协议迁移及其前置迁移必须存在。");
        var recoverySchema = migrations[..(recoveryIndex + 1)];
        var migrator = context.GetService<IMigrator>();
        // Only this migration window owns the heavy-operation lease. Recovery still races rollback.
        // Acquire before starting the original ten-second race budget; database cleanup occurs after release.
        await using var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true);
        // Newer empty schemas do not change the migration whose rollback protection is under test.
        // Hosts have already produced the fact on the current schema; no host commands run in this window.
        using (var preparationBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await migrator.MigrateAsync(migrations[recoveryIndex], preparationBudget.Token);
        }
        await using var control = new NpgsqlConnection(database.ConnectionString);
        await control.OpenAsync();
        await using (var arrange = new NpgsqlCommand($"""
            SELECT pg_advisory_lock(103064);
            CREATE FUNCTION {source}.pause_recovery_for_rollback() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(103064); RETURN NEW; END $$;
            CREATE TRIGGER pause_recovery_for_rollback BEFORE INSERT ON {source}.fact_recovery_receipts
                FOR EACH ROW EXECUTE FUNCTION {source}.pause_recovery_for_rollback();
            """, control)) { await arrange.ExecuteNonQueryAsync(); }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var recovering = delivery.RecoverAsync(request, "recovery-operator", now, null, cancellation.Token);
        Task? rollback = null;
        try
        {
            await WaitForBlockedCommandAsync(control, "advisory", cancellation.Token);
            rollback = migrator.MigrateAsync(migrations[recoveryIndex - 1], cancellation.Token);
            await WaitForBlockedCommandAsync(control, "relation", cancellation.Token);
            await ReleaseAsync(control);
            var accepted = await recovering;
            Assert.True(accepted.IsSuccess);
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => rollback);
            Assert.Equal("P0001", rejected.SqlState);
            Assert.Equal(source + "_fact_recovery_history_exists", rejected.ConstraintName);
            Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
            Assert.Equal(recoverySchema, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
            var retained = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == original.Id);
            Assert.Equal(original.Payload, retained.Payload);
            Assert.Equal(original.OccurredAt, retained.OccurredAt);
            Assert.Equal(1, retained.RetryRevision);
        }
        finally
        {
            cancellation.Cancel();
            await ReleaseAsync(control);
            try { await recovering; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            if (rollback is not null)
            {
                try { await rollback; }
                catch (PostgresException) { }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }
        // Reapplying later migrations is separate from the original ten-second race budget.
        using var upgradeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await migrator.MigrateAsync(cancellationToken: upgradeBudget.Token);
        Assert.Equal(migrations, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
    }

    private static async Task ReleaseAsync(NpgsqlConnection control)
    {
        await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(103064)", control);
        await release.ExecuteNonQueryAsync();
    }

    private static async Task WaitForBlockedCommandAsync(NpgsqlConnection control, string waitEvent, CancellationToken token)
    {
        while (true)
        {
            // This only detects the owned fault barrier; results are asserted through Recover and IMigrator.
            await using var blocked = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                    AND application_name = 'nsn-recovery-rollback-race' AND wait_event = @wait;
                """, control);
            blocked.Parameters.AddWithValue("wait", waitEvent);
            if ((long)(await blocked.ExecuteScalarAsync(token))! > 0) { return; }
            await Task.Delay(10, token);
        }
    }
}
