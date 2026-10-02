using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PlatformPersistenceJourneyTests
{
    [PostgresFact]
    public async Task OverlappingWrites_WithoutClientVersions_StillRejectDatabaseConflicts()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "settings-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "settings-root-password");
        var uri = new Uri("/api/platform/settings/mail.sender", UriKind.Relative);
        using var created = await client.PutAsJsonAsync(uri, new { value = "original", description = "original" });
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);

        // 只锁本测试临时库中的目标行：读取仍可完成，两个保存必须停在同一个版本之后。
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM platform.global_settings FOR UPDATE", blocker, transaction))
        {
            await command.ExecuteScalarAsync();
        }
        var writes = new[] { "first", "second" }.Select(value =>
            client.PutAsJsonAsync(uri, new { value, description = value })).ToArray();
        try
        {
            await using var observer = new NpgsqlConnection(database.ConnectionString);
            await observer.OpenAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                // 这里只确认重叠屏障；最终提交和冲突仍通过 HTTP 判断。
                await using var command = new NpgsqlCommand("""
                    SELECT count(*) FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock'
                    AND query LIKE '%UPDATE%global_settings%'
                    """, observer);
                if ((long)(await command.ExecuteScalarAsync(timeout.Token))! == 2) { break; }
                await Task.Delay(20, timeout.Token);
            }
        }
        finally { await transaction.RollbackAsync(); }
        var responses = await Task.WhenAll(writes);
        try
        {
            Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }, responses.Select(response => response.StatusCode).Order());
        }
        finally { foreach (var response in responses) { response.Dispose(); } }
        using var read = await client.GetAsync(uri);
        var setting = await read.Content.ReadApiDataAsync();
        Assert.Contains(setting.GetProperty("value").GetString(), new[] { "first", "second" });
        Assert.Equal(setting.GetProperty("value").GetString(), setting.GetProperty("description").GetString());
        Assert.Equal(3, setting.GetProperty("version").GetInt64());
    }

    [PostgresFact]
    public async Task UnavailablePlatformDatabase_RefusesStartup_WithSanitizedDiagnostic()
    {
        await using var identity = await IdentityJourneyDatabase.CreateAsync();
        await identity.MigrateAsync();
        await using var app = new PersistentIdentityApp(identity.ConnectionString,
            platformConnectionString: "Host=127.0.0.1;Port=1;Database=unavailable;Timeout=1");
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("Platform 数据库不可用", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Port=1", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryAdapter_EnforcesTheSameConditionalWriteProtocol()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyConditionalWritesAsync(client);
    }

    [PostgresFact]
    public async Task FailedDatabaseCommit_DoesNotLeavePartialValueOrDescription_AndNextWriteRecovers()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "settings-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "settings-root-password");
        var uri = new Uri("/api/platform/settings/mail.sender", UriKind.Relative);
        using var created = await client.PutAsJsonAsync(uri, new { value = "original", description = "original" });
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "ALTER TABLE platform.global_settings ADD CONSTRAINT test_reject_save CHECK (\"Value\" <> 'reject-save')", connection);
            await command.ExecuteNonQueryAsync();
        }
        using var rejected = await client.PutAsJsonAsync(uri, new { value = "reject-save", description = "should-rollback", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("http.500", error.GetProperty("errorCode").GetString());
        using var read = await client.GetAsync(uri);
        var unchanged = await read.Content.ReadApiDataAsync();
        Assert.Equal("original", unchanged.GetProperty("value").GetString());
        Assert.Equal("original", unchanged.GetProperty("description").GetString());
        Assert.Equal(1, unchanged.GetProperty("version").GetInt64());
        using var recovered = await client.PutAsJsonAsync(uri, new { value = "recovered", description = "recovered", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
        using var final = await client.GetAsync(uri);
        Assert.Equal("recovered", (await final.Content.ReadApiDataAsync()).GetProperty("value").GetString());
    }

    [PostgresFact]
    public async Task ConcurrentFirstWrites_CreateExactlyOneRegistration_AndReportConflict()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "settings-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "settings-root-password");
        var uri = new Uri("/api/platform/settings/mail.sender", UriKind.Relative);
        var writes = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            client.PutAsJsonAsync(uri, new { value = "value-" + index, description = "value-" + index, expectedVersion = 0 })));
        try
        {
            Assert.Single(writes, response => response.StatusCode == HttpStatusCode.NoContent);
            Assert.Equal(5, writes.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }
        finally { foreach (var response in writes) { response.Dispose(); } }
        using var list = await client.GetAsync(new Uri("/api/platform/settings/?scope=mail", UriKind.Relative));
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        var setting = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal(setting.GetProperty("value").GetString(), setting.GetProperty("description").GetString());
        Assert.Equal(1, setting.GetProperty("version").GetInt64());
    }

    [PostgresFact]
    public async Task ConditionalWrites_RejectStaleChanges_AndDoNotAdvanceNoOpVersion()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "settings-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "settings-root-password");
        await VerifyConditionalWritesAsync(client);
    }

    private static async Task VerifyConditionalWritesAsync(HttpClient client)
    {
        var uri = new Uri("/api/platform/settings/mail.sender", UriKind.Relative);
        using var created = await client.PutAsJsonAsync(uri, new { value = "original", description = "original", expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        var writes = await Task.WhenAll(new[] { "first", "second" }.Select(value =>
            client.PutAsJsonAsync(uri, new { value, description = value, expectedVersion = 1 })));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }, writes.Select(response => response.StatusCode).Order());
        }
        finally { foreach (var response in writes) { response.Dispose(); } }

        using var read = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var setting = await read.Content.ReadApiDataAsync();
        var value = setting.GetProperty("value").GetString();
        var version = setting.GetProperty("version").GetInt64();
        Assert.Contains(value, new[] { "first", "second" });
        Assert.Equal(value, setting.GetProperty("description").GetString());
        using var stale = await client.PutAsJsonAsync(uri, new { value = "stale", description = "stale", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var error = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("platform.setting.conflict", error.GetProperty("errorCode").GetString());
        using var staleClear = await client.DeleteAsync(new Uri(uri + "?expectedVersion=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, staleClear.StatusCode);
        using var noOp = await client.PutAsJsonAsync(uri, new { value, description = value, expectedVersion = version });
        Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode);
        using var unchanged = await client.GetAsync(uri);
        Assert.Equal(version, (await unchanged.Content.ReadApiDataAsync()).GetProperty("version").GetInt64());
        using var clear = await client.DeleteAsync(new Uri(uri + "?expectedVersion=" + version, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        using var cleared = await client.GetAsync(uri);
        var final = await cleared.Content.ReadApiDataAsync();
        Assert.Equal(JsonValueKind.Null, final.GetProperty("value").ValueKind);
        Assert.Equal(value, final.GetProperty("description").GetString());
        Assert.Equal(version + 1, final.GetProperty("version").GetInt64());
    }

    [PostgresFact]
    public async Task PlatformOutage_ChangesReadinessButNotLiveness_AndRecoversIndependentlyOfIdentity()
    {
        await using var identity = await IdentityJourneyDatabase.CreateAsync();
        await identity.MigrateAsync();
        await using var platform = await IdentityJourneyDatabase.CreateAsync();
        var migration = await IdentityJourneyDatabase.RunMigrationAsync(platform.ConnectionString, "Platform");
        Assert.Equal(0, migration.ExitCode);
        await using var app = new PersistentIdentityApp(identity.ConnectionString, "settings-root-password", platform.ConnectionString);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "settings-root-password");
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await platform.SetAvailableAsync(false);
        try
        {
            using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using var down = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "settings-root-password");
        }
        finally
        {
            await platform.SetAvailableAsync(true);
        }
        using var recovered = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [PostgresFact]
    public async Task PlatformWithoutAppliedMigrations_RefusesToStart_WithoutMigratingAtStartup()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        var identity = await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString);
        Assert.Equal(0, identity.ExitCode);
        await using var app = new PersistentIdentityApp(database.ConnectionString);
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("migrate-platform", error.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task UpdatedAndClearedSettings_PreserveRegistrationAndDescription_AfterRepeatedMigrationAndRestart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using (var first = new PersistentIdentityApp(database.ConnectionString, "settings-root-password"))
        {
            using var client = first.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "settings-root-password");
            foreach (var key in new[] { "mail.sender", "mail.subject", "mail-temp.sender" })
            {
                using var created = await client.PutAsJsonAsync(new Uri("/api/platform/settings/" + key, UriKind.Relative),
                    new { value = "original", description = "Retained metadata" });
                Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
            }
            using var updated = await client.PutAsJsonAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative),
                new { value = "updated", description = "Updated metadata" });
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
            using var cleared = await client.DeleteAsync(new Uri("/api/platform/settings/mail.subject", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        }

        await database.MigrateAsync();
        await using var restarted = new PersistentIdentityApp(database.ConnectionString, "settings-root-password");
        using var after = restarted.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(after, "journey-root", "settings-root-password");
        using var list = await after.GetAsync(new Uri("/api/platform/settings/?scope=mail", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        var settings = page.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(new[] { "mail.sender", "mail.subject" }, settings.Select(setting => setting.GetProperty("key").GetString()));
        Assert.Equal("updated", settings[0].GetProperty("value").GetString());
        Assert.Equal("Updated metadata", settings[0].GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, settings[1].GetProperty("value").ValueKind);
        Assert.Equal("Retained metadata", settings[1].GetProperty("description").GetString());
    }

    [PostgresFact]
    public async Task CommittedSetting_IsAvailableAfterHostRestart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "settings-root-password"))
        {
            var client = first.Client;
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "settings-root-password");
            using var written = await client.PutAsJsonAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative),
                new { value = "sender@example.invalid", description = "Sender display metadata" });
            Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);
        }

        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "settings-root-password");
        var after = restarted.Client;
        await PlatformSettingsAccessTests.LoginAsync(after, "journey-root", "settings-root-password");
        using var read = await after.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("sender@example.invalid", (await read.Content.ReadApiDataAsync()).GetProperty("value").GetString());
        using var list = await after.GetAsync(new Uri("/api/platform/settings/?scope=mail", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        var setting = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal("mail.sender", setting.GetProperty("key").GetString());
        Assert.Equal("Sender display metadata", setting.GetProperty("description").GetString());
    }
}
