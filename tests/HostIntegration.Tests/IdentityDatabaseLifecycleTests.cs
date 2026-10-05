using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityDatabaseLifecycleTests
{
    [PostgresFact]
    public async Task DatabaseDisposal_UsesFreshAdminConnection_WhenAnIdlePooledSessionWasClosed()
    {
        var original = Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable);
        await using var adminDatabase = await IdentityJourneyDatabase.CreateAsync();
        var admin = new NpgsqlConnectionStringBuilder(adminDatabase.ConnectionString)
        {
            ApplicationName = "nsn-admin-lifecycle-" + Guid.NewGuid().ToString("N"),
            Pooling = true,
            MaxPoolSize = 1,
        };
        var controllerOptions = new NpgsqlConnectionStringBuilder(adminDatabase.ConnectionString) { Pooling = false };
        await using var controller = new NpgsqlConnection(controllerOptions.ConnectionString);
        await controller.OpenAsync();
        IdentityJourneyDatabase? database = null;
        var disposed = false;
        try
        {
            Environment.SetEnvironmentVariable(TestPostgres.ConnectionStringVariable, admin.ConnectionString);
            database = await IdentityJourneyDatabase.CreateAsync();
            Assert.True(new NpgsqlConnectionStringBuilder(database.ConnectionString).Pooling);
            var name = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
            await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @database)", controller);
            exists.Parameters.AddWithValue("database", name);
            Assert.True((bool)(await exists.ExecuteScalarAsync())!);

            int backend;
            await using (var pooled = new NpgsqlConnection(admin.ConnectionString))
            {
                await pooled.OpenAsync();
                await using var identity = new NpgsqlCommand("SELECT pg_backend_pid()", pooled);
                backend = (int)(await identity.ExecuteScalarAsync())!;
            }
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Only the seeded session in our own temporary admin database can be terminated.
            await using var terminate = new NpgsqlCommand("""
                SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE pid = @pid AND datname = @database AND application_name = @application)
                    THEN pg_terminate_backend(@pid) ELSE false END
                """, controller);
            terminate.Parameters.AddWithValue("pid", backend);
            terminate.Parameters.AddWithValue("database", admin.Database!);
            terminate.Parameters.AddWithValue("application", admin.ApplicationName!);
            Assert.True((bool)(await terminate.ExecuteScalarAsync(budget.Token))!);
            await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid = @pid AND datname = @database", controller);
            waiting.Parameters.AddWithValue("pid", backend);
            waiting.Parameters.AddWithValue("database", admin.Database!);
            while ((long)(await waiting.ExecuteScalarAsync(budget.Token))! != 0)
            {
                await Task.Delay(10, budget.Token);
            }

            await database.DisposeAsync();
            disposed = true;
            Assert.False((bool)(await exists.ExecuteScalarAsync())!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestPostgres.ConnectionStringVariable, original);
            if (database is not null && !disposed)
            {
                var name = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
                await using var cleanup = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", controller);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
