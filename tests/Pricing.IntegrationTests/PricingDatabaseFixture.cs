using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingDatabaseFixture : IAsyncLifetime
{
    private readonly string _name = $"nsn_pricing_test_{Guid.NewGuid():N}";
    private bool _created;
    public bool MigrateOnInitialize { get; init; } = true;
    public string ConnectionString => new NpgsqlConnectionStringBuilder(TestPostgres.ConnectionString()) { Database = _name }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable))) { return; }
        await ExecuteAdminAsync($"CREATE DATABASE \"{_name}\"");
        _created = true;
        if (MigrateOnInitialize) { await PricingDatabase.MigrateAsync(ConnectionString); }
    }

    public async Task DisposeAsync()
    {
        if (!_created) { return; }
        using var pool = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(pool);
        await ExecuteAdminAsync($"DROP DATABASE \"{_name}\" WITH (FORCE)");
    }

    public async Task ResetAsync()
    {
        if (!_created) { return; }
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE pricing.tasks, pricing.quotes, pricing.inbox, pricing.outbox, pricing.cache_invalidations CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task SetAvailableAsync(bool available)
    {
        await ExecuteAdminAsync($"ALTER DATABASE \"{_name}\" ALLOW_CONNECTIONS {(available ? "true" : "false")}");
        if (!available) { await ExecuteAdminAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_name}'"); }
        using var pool = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(pool);
    }

    private static async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.ConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<NpgsqlConnection> PauseResultWriteAsync()
    {
        // 会话锁模拟提交阶段停顿；只作用于当前测试的独立数据库。
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT pg_advisory_lock(742193);
                CREATE OR REPLACE FUNCTION pricing.pause_test_result() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  PERFORM pg_advisory_xact_lock(742193);
                  RETURN NEW;
                END $$;
                CREATE OR REPLACE TRIGGER pause_test_result BEFORE UPDATE ON pricing.quotes
                FOR EACH ROW EXECUTE FUNCTION pricing.pause_test_result();
                """, connection);
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task RejectTaskCompletionAsync(Guid taskId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE OR REPLACE FUNCTION pricing.reject_test_completion() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."TaskId" = '{taskId:D}'::uuid AND NEW."State" = 'Succeeded' THEN
                RAISE EXCEPTION 'injected completion failure';
              END IF;
              RETURN NEW;
            END $$;
            CREATE OR REPLACE TRIGGER reject_test_completion BEFORE UPDATE ON pricing.tasks
            FOR EACH ROW EXECUTE FUNCTION pricing.reject_test_completion();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task RejectTaskInsertAsync(Guid requestId)
    {
        // 数据库边界的故障注入：业务写入可执行，任务写入在同一事务中被数据库拒绝。
        // 断言仍只读取公开查询接口，不通过表内容验证结果。
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var sql = $"""
            CREATE OR REPLACE FUNCTION pricing.reject_test_task() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."TaskId" = '{requestId:D}'::uuid THEN RAISE EXCEPTION 'injected task write failure'; END IF;
              RETURN NEW;
            END $$;
            CREATE OR REPLACE TRIGGER reject_test_task BEFORE INSERT ON pricing.tasks
            FOR EACH ROW EXECUTE FUNCTION pricing.reject_test_task();
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task AllowTaskInsertsAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DROP TRIGGER IF EXISTS reject_test_task ON pricing.tasks", connection);
        await command.ExecuteNonQueryAsync();
    }
}
