using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingDatabaseFixture : IAsyncLifetime
{
    private readonly string _name = $"nsn_costing_test_{Guid.NewGuid():N}";
    private bool _created;
    public bool MigrateOnInitialize { get; init; } = true;
    public string ConnectionString => new NpgsqlConnectionStringBuilder(TestPostgres.ConnectionString()) { Database = _name }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable))) { return; }
        await ExecuteAdminAsync($"CREATE DATABASE \"{_name}\"");
        _created = true;
        if (MigrateOnInitialize) { await CostingDatabase.MigrateAsync(ConnectionString); }
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
        await using var command = new NpgsqlCommand("TRUNCATE costing.tasks, costing.sheets, costing.outbox, costing.inbox, costing.schedule_receipts CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.ConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task RejectOutboxWriteAsync(Guid messageId, bool reject)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var sql = reject ? $"""
            CREATE OR REPLACE FUNCTION costing.reject_test_outbox() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."Id" = '{messageId:D}'::uuid THEN RAISE EXCEPTION 'injected outbox failure'; END IF;
              RETURN NEW;
            END $$;
            CREATE OR REPLACE TRIGGER reject_test_outbox BEFORE INSERT ON costing.outbox
            FOR EACH ROW EXECUTE FUNCTION costing.reject_test_outbox();
            """ : "DROP TRIGGER IF EXISTS reject_test_outbox ON costing.outbox";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
