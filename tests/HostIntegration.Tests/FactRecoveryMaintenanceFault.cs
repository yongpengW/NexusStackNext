using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

internal sealed class FactRecoveryMaintenanceFault(NpgsqlConnection connection, string source) : IAsyncDisposable
{
    internal static async Task<FactRecoveryMaintenanceFault> InstallAsync(NpgsqlConnection connection, string source)
    {
        Assert.Contains(source, new[] { "files", "scheduling" });
        var fault = new FactRecoveryMaintenanceFault(connection, source);
        await using var inject = new NpgsqlCommand($"""
            CREATE FUNCTION {source}.reject_maintained_recovery_release() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."RetainedRecords" < OLD."RetainedRecords" THEN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Sensitive controlled maintenance failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_maintained_recovery_release BEFORE UPDATE ON {source}.fact_recovery_control
                FOR EACH ROW EXECUTE FUNCTION {source}.reject_maintained_recovery_release();
            """, connection);
        await inject.ExecuteNonQueryAsync();
        return fault;
    }

    internal async Task AllowSingleReleaseAsync()
    {
        // Keep the guard installed: an implementation ignoring BatchSize=1 cannot complete both receipts.
        await using var repair = new NpgsqlCommand($"""
            CREATE OR REPLACE FUNCTION {source}.reject_maintained_recovery_release() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."RetainedRecords" - NEW."RetainedRecords" > 1 THEN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Recovery cleanup exceeded its single-record batch';
                END IF;
                RETURN NEW;
            END $$;
            """, connection);
        await repair.ExecuteNonQueryAsync();
    }

    internal Task<HealthReportEntry> WaitForFailureAsync(HealthCheckService health, CancellationToken cancellationToken)
        => WaitAsync(health, entry => entry.Status == HealthStatus.Degraded, cancellationToken);

    internal Task<HealthReportEntry> WaitForReleaseAsync(HealthCheckService health, long count, CancellationToken cancellationToken)
        => WaitAsync(health, entry => (long)entry.Data["releasedRequests"] == count, cancellationToken);

    private async Task<HealthReportEntry> WaitAsync(HealthCheckService health, Func<HealthReportEntry, bool> ready,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var report = await health.CheckHealthAsync(registration => registration.Name == source + "-recovery-cleanup", cancellationToken);
            var entry = Assert.Single(report.Entries).Value;
            if (ready(entry)) { return entry; }
            await Task.Delay(50, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var remove = new NpgsqlCommand($"""
            DROP TRIGGER reject_maintained_recovery_release ON {source}.fact_recovery_control;
            DROP FUNCTION {source}.reject_maintained_recovery_release();
            """, connection);
        await remove.ExecuteNonQueryAsync();
    }
}
