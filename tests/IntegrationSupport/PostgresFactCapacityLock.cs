using Npgsql;
using Xunit;

namespace NexusStackNext.IntegrationSupport;

/// <summary>只在调用方的隔离测试库中占住所属容量行；退出作用域释放。</summary>
internal sealed class PostgresFactCapacityLock(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
{
    private bool _released;

    public static async Task<PostgresFactCapacityLock> AcquireAsync(string connectionString, string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (schema.Any(character => !char.IsAsciiLetterLower(character))) { throw new ArgumentException("Expected a fixed lowercase schema.", nameof(schema)); }
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"SELECT 1 FROM {schema}.fact_capacity WHERE \"Id\" = 1 FOR UPDATE", connection, transaction);
            Assert.Equal(1, await command.ExecuteScalarAsync().ConfigureAwait(false));
            return new PostgresFactCapacityLock(connection, transaction);
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task WaitForWriterAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))", connection, transaction);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!) { return; }
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReleaseAsync()
    {
        if (_released) { return; }
        await transaction.RollbackAsync().ConfigureAwait(false);
        _released = true;
    }

    public async ValueTask DisposeAsync()
    {
        try { await ReleaseAsync().ConfigureAwait(false); }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
