using Npgsql;

namespace NexusStackNext.IntegrationSupport;

/// <summary>只在测试独立数据库内暂停指定任务的更新，观察真实锁等待。</summary>
internal sealed class PostgresTaskBarrier : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _schema;
    private bool _released;

    private PostgresTaskBarrier(NpgsqlConnection connection, string schema)
    {
        _connection = connection;
        _schema = schema;
    }

    public static async Task<PostgresTaskBarrier> InstallAsync(string connectionString, string schema, Guid taskId)
    {
        if (schema is not ("costing" or "pricing")) { throw new ArgumentException("Unsupported task context.", nameof(schema)); }
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"""
                SELECT pg_advisory_lock(480048);
                CREATE OR REPLACE FUNCTION "{schema}".pause_task_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."TaskId" = '{taskId:D}'::uuid THEN PERFORM pg_advisory_xact_lock(480048); END IF;
                  RETURN NEW;
                END $$;
                CREATE OR REPLACE TRIGGER pause_task_update BEFORE UPDATE ON "{schema}".tasks
                FOR EACH ROW EXECUTE FUNCTION "{schema}".pause_task_update();
                """, connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return new PostgresTaskBarrier(connection, schema);
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task WaitForWriterAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))
                """, _connection);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false))!) { return; }
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
    }

    public async Task WaitForWaitersAsync(int minimum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimum, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", _connection);
            if ((long)(await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false))! >= minimum) { return; }
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
    }

    public async Task ReleaseAsync()
    {
        if (_released) { return; }
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(480048)", _connection);
        await command.ExecuteScalarAsync().ConfigureAwait(false);
        _released = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ReleaseAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"""
                DROP TRIGGER IF EXISTS pause_task_update ON "{_schema}".tasks;
                DROP FUNCTION IF EXISTS "{_schema}".pause_task_update();
                """, _connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { await _connection.DisposeAsync().ConfigureAwait(false); }
    }
}
