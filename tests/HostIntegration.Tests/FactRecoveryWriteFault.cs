using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

internal sealed record FactRecoveryWriteFault(string Table, string Operation, string State, string Constraint)
{
    internal static IReadOnlyList<FactRecoveryWriteFault> Cases { get; } =
    [
        new("fact_recovery_receipts", "INSERT", "23514", "controlled_receipt_failure"),
        new("fact_recovery_control", "UPDATE", "23514", "controlled_ledger_failure"),
        new("fact_recovery_receipts", "INSERT", "55P03", "foreign_receipt_lock_failure"),
    ];

    internal async Task<IAsyncDisposable> InstallAsync(NpgsqlConnection connection, string source)
    {
        Assert.Contains(source, new[] { "platform", "identity", "files", "scheduling", "costing", "pricing" });
        await using var inject = new NpgsqlCommand($"""
            CREATE FUNCTION {source}.reject_recovery_publish() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION USING ERRCODE = '{State}', CONSTRAINT = '{Constraint}',
                MESSAGE = 'Controlled recovery persistence failure'; END $$;
            CREATE TRIGGER reject_recovery_publish BEFORE {Operation} ON {source}.{Table}
                FOR EACH ROW EXECUTE FUNCTION {source}.reject_recovery_publish();
            """, connection);
        await inject.ExecuteNonQueryAsync();
        return new InstalledFault(connection, source, Table);
    }

    private sealed class InstalledFault(NpgsqlConnection connection, string source, string table) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var repair = new NpgsqlCommand($"""
                DROP TRIGGER reject_recovery_publish ON {source}.{table};
                DROP FUNCTION {source}.reject_recovery_publish();
                """, connection);
            await repair.ExecuteNonQueryAsync();
        }
    }
}
