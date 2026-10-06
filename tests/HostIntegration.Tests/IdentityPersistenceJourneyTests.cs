using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Infrastructure.Persistence;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.PlatformHost;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Infrastructure.Persistence;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityPersistenceJourneyTests
{
    [PostgresFact]
    public async Task TerminatingTheProcess_PreservesCommittedUsersAndRevocation()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        JsonElement tokens;
        await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString))
        {
            await CreateAsync(first.Client, "/api/identity/users", new { userName = "process-user", password = "journey-test-password" });
            tokens = await AuthenticateAsync(first.Client, "process-user", "journey-test-password");
            using var logout = await first.Client.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString);
        restarted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        using var denied = await restarted.Client.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var fresh = await AuthenticateAsync(restarted.Client, "process-user", "journey-test-password");
        Assert.Equal(tokens.GetProperty("userId").ReadHttpInt64(), fresh.GetProperty("userId").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task RefreshReplay_RevokesReplacementTokensAcrossRestart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        JsonElement original;
        JsonElement replacement;
        await using (var app = new PersistentIdentityApp(database.ConnectionString))
        {
            using var client = app.CreateClient();
            await CreateAsync(client, "/api/identity/users", new { userName = "replay-user", password = "journey-test-password" });
            original = await AuthenticateAsync(client, "replay-user", "journey-test-password");
            var request = new { refreshToken = original.GetProperty("refreshToken").GetString() };
            using var rotated = await client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            replacement = await rotated.Content.ReadApiDataAsync();
            using var replay = await client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        }

        await using var restarted = new PersistentIdentityApp(database.ConnectionString);
        using var after = restarted.CreateClient();
        foreach (var tokens in new[] { original, replacement })
        {
            after.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
            using var denied = await after.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var refresh = await after.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative),
                new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
            Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        }

        await AuthenticateAsync(after, "replay-user", "journey-test-password");
        using var staleReplay = await after.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative),
            new { refreshToken = original.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, staleReplay.StatusCode);
        using var freshAccess = await after.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, freshAccess.StatusCode);
    }

    [PostgresFact]
    public async Task FailedPasswordAttempts_AreCountedAcrossRestart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using (var app = new PersistentIdentityApp(database.ConnectionString))
        {
            using var client = app.CreateClient();
            await CreateAsync(client, "/api/identity/users", new { userName = "locked-user", password = "journey-test-password" });
            await FailLoginAsync(client, 2);
        }

        await using var restarted = new PersistentIdentityApp(database.ConnectionString);
        using var after = restarted.CreateClient();
        await FailLoginAsync(after, 3);
        using var locked = await after.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative),
            new { userName = "locked-user", password = "journey-test-password" });
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        var problem = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("锁定", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    private static async Task FailLoginAsync(HttpClient client, int count)
    {
        for (var index = 0; index < count; index++)
        {
            using var failed = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative),
                new { userName = "locked-user", password = "incorrect-password" });
            Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        }
    }

    [PostgresFact]
    public async Task RoleAndMenuGrants_SurviveRestart_WithoutResettingRootCredentials()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var credentials = new { userName = "persistent-operator", password = "journey-test-password" };
        long userId;
        long menuId;
        const string Permission = "/api/identity/users/{userId}/permissions";
        await using (var app = new PersistentIdentityApp(database.ConnectionString, "original-root-password"))
        {
            using var root = app.CreateClient();
            using var user = app.CreateClient();
            await AuthenticateAsync(root, "journey-root", "original-root-password");
            var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Persistent menu", sortOrder = 1 });
            menuId = menu.GetProperty("menuId").ReadHttpInt64();
            await CreateAsync(root, "/api/identity/api-resources", new { path = Permission, method = "GET", menuId });
            var role = await CreateAsync(root, "/api/identity/roles", new { code = "persistent-reader", name = "Reader" });
            var roleId = role.GetProperty("roleId").ReadHttpInt64();
            using var grant = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{menuId}", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
            userId = (await CreateAsync(user, "/api/identity/users", credentials)).GetProperty("userId").ReadHttpInt64();
            await AuthenticateAsync(user, credentials.userName, credentials.password);
            using var denied = await user.GetAsync(new Uri($"/api/identity/users/{userId}/permissions", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var assigned = await root.PostAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
            using var allowed = await user.GetAsync(new Uri($"/api/identity/users/{userId}/permissions", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        await using var restarted = new PersistentIdentityApp(database.ConnectionString, "changed-bootstrap-password");
        using var after = restarted.CreateClient();
        await AuthenticateAsync(after, credentials.userName, credentials.password);
        var permissions = await after.GetFromJsonAsync<JsonElement>(new Uri($"/api/identity/users/{userId}/permissions", UriKind.Relative));
        Assert.Contains("/api/identity/users/{userid}/permissions:GET", permissions.GetProperty("data").GetProperty("keys").EnumerateArray().Select(static key => key.GetString()));
        await AuthenticateAsync(after, "journey-root", "original-root-password");
        var menus = await after.GetFromJsonAsync<JsonElement>(new Uri("/api/identity/menus", UriKind.Relative));
        Assert.Equal(1, menus.GetProperty("data").GetProperty("count").GetInt32());
        Assert.Equal(menuId, Assert.Single(menus.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("menuId").ReadHttpInt64());
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static async Task<JsonElement> AuthenticateAsync(HttpClient client, string userName, string password)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), new { userName, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadApiDataAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return tokens;
    }

    [PostgresFact]
    public async Task DatabaseOutage_ChangesReadinessButNotLiveness_AndRecovers()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString);
        using var client = app.CreateClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await database.SetAvailableAsync(false);
        try
        {
            using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using var down = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        }
        finally
        {
            await database.SetAvailableAsync(true);
        }

        using var recovered = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [PostgresFact]
    public async Task Logout_RemainsRevokedAfterRestart_AndFreshLoginWorks()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var credentials = new { userName = "logout-user", password = "journey-test-password" };
        JsonElement tokens;
        await using (var app = new PersistentIdentityApp(database.ConnectionString))
        {
            using var client = app.CreateClient();
            using var registered = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), credentials);
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            using var login = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), credentials);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            tokens = await login.Content.ReadApiDataAsync();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
            using var logout = await client.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        await using var restarted = new PersistentIdentityApp(database.ConnectionString);
        using var second = restarted.CreateClient();
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        using var oldAccess = await second.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Unauthorized, oldAccess.StatusCode);
        using var oldRefresh = await second.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative),
            new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, oldRefresh.StatusCode);
        using var fresh = await second.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), credentials);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var body = await fresh.Content.ReadApiDataAsync();
        using var freshRefresh = await second.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative),
            new { refreshToken = body.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, freshRefresh.StatusCode);
        var renewed = await freshRefresh.Content.ReadApiDataAsync();
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", renewed.GetProperty("accessToken").GetString());
        using var accepted = await second.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [PostgresFact]
    public async Task HostWithoutAppliedMigrations_RefusesToStart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString);
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("migrate-identity", error.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task MigratedDatabase_PreservesUserAcrossHostRestart_AndRepeatedMigration()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateThroughCliAsync();
        var credentials = new { userName = "persistent-user", password = "journey-test-password" };
        long userId;
        await using (var app = new PersistentIdentityApp(database.ConnectionString))
        {
            using var client = app.CreateClient();
            using var registered = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), credentials);
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            userId = (await registered.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
        }

        await database.MigrateThroughCliAsync();
        await using var restarted = new PersistentIdentityApp(database.ConnectionString);
        using var second = restarted.CreateClient();
        using var login = await second.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), credentials);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(userId, (await login.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64());
    }
}

internal class PersistentIdentityApp : WebApplicationFactory<PlatformHostMarker>
{
    private readonly string _connectionString;
    private readonly string _platformConnectionString;
    private readonly string _filesConnectionString;
    private readonly string _auditingConnectionString;
    private readonly string _schedulingConnectionString;
    private readonly string? _rootPassword;
    private readonly bool _schedulingWorkerEnabled;
    private readonly IClock? _schedulingClock;

    public PersistentIdentityApp(string connectionString, string? rootPassword = null, string? platformConnectionString = null,
        string? filesConnectionString = null, string? auditingConnectionString = null, bool schedulingWorkerEnabled = true,
        string? schedulingConnectionString = null, IClock? schedulingClock = null)
    {
        _connectionString = connectionString;
        _platformConnectionString = platformConnectionString ?? connectionString;
        _filesConnectionString = filesConnectionString ?? connectionString;
        _auditingConnectionString = auditingConnectionString ?? connectionString;
        _schedulingConnectionString = schedulingConnectionString ?? connectionString;
        _rootPassword = rootPassword;
        _schedulingWorkerEnabled = schedulingWorkerEnabled;
        _schedulingClock = schedulingClock;
        UseKestrel(0);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Identity:Storage:Provider"] = "Postgres",
                ["Platform:Storage:Provider"] = "Postgres",
                ["Platform:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Identity:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Files:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Scheduling:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Files:Storage:Provider"] = "Postgres",
                ["Auditing:Storage:Provider"] = "Postgres",
                ["Scheduling:Storage:Provider"] = "Postgres",
                ["Scheduling:Worker:Enabled"] = _schedulingWorkerEnabled.ToString(),
                ["ConnectionStrings:Identity"] = _connectionString,
                ["ConnectionStrings:Platform"] = _platformConnectionString,
                ["ConnectionStrings:Files"] = _filesConnectionString,
                ["ConnectionStrings:Auditing"] = _auditingConnectionString,
                ["OperationJournal:Storage:Provider"] = "Postgres",
                ["ConnectionStrings:OperationJournal"] = _connectionString,
                ["ConnectionStrings:Scheduling"] = _schedulingConnectionString,
            }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        if (_schedulingClock is { } clock)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TaskRegistry>();
                services.RemoveAll<ScheduleRunner>();
                services.AddScoped(provider => new TaskRegistry(provider.GetRequiredService<IScheduledTaskStore>(),
                    provider.GetRequiredService<IIdGenerator>(), clock, provider.GetRequiredService<IScheduleCalendar>(),
                    provider.GetRequiredService<IExecutionContext>()));
                services.AddScoped(provider => new ScheduleRunner(provider.GetRequiredService<IScheduledTaskStore>(),
                    clock, provider.GetRequiredService<IScheduleCalendar>(), provider.GetRequiredService<IBackgroundExecutionObservation>(),
                    provider.GetRequiredService<IExecutionContext>()));
            });
        }
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Jwt:SigningKey"] = "integration-test-signing-key-long-enough-for-hs256",
                ["Identity:Root:UserName"] = _rootPassword is null ? null : "journey-root",
                ["Identity:Root:Password"] = _rootPassword,
            }));
    }
}

internal sealed class IdentityJourneyDatabase : IAsyncDisposable
{
    private readonly string _name = $"nsn_identity_journey_{Guid.NewGuid():N}";
    private readonly string _admin = TestPostgres.ConnectionString();

    public string ConnectionString => new NpgsqlConnectionStringBuilder(_admin) { Database = _name }.ConnectionString;

    public static async Task<IdentityJourneyDatabase> CreateAsync()
    {
        var database = new IdentityJourneyDatabase();
        await database.ExecuteAdminAsync($"CREATE DATABASE \"{database._name}\"");
        return database;
    }

    internal async Task<IdentityJourneyDatabase> CopyAsync()
    {
        var database = new IdentityJourneyDatabase();
        // Small immutable templates use WAL_LOG; FILE_COPY would force shared-server checkpoints.
        // Generated names belong to this fixture; no caller can select another database as a template.
        await database.ExecuteAdminAsync($"CREATE DATABASE \"{database._name}\" TEMPLATE \"{_name}\" STRATEGY WAL_LOG ALLOW_CONNECTIONS true");
        return database;
    }

    public async Task MigrateAsync()
    {
        await using var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true);
        // 未复用结构的旅程复用测试进程中的 EF 模型；每次仍对独立空库执行真实迁移。
        // CLI 契约测试显式使用 MigrateThroughCliAsync，避免反复启动六个进程。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await MigrateContextAsync(new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNexusStackPostgres(ConnectionString, IdentityDbContext.SchemaName).Options), timeout.Token);
        await MigrateContextAsync(new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres(ConnectionString, PlatformDbContext.SchemaName).Options), timeout.Token);
        await MigrateContextAsync(new FilesDbContext(new DbContextOptionsBuilder<FilesDbContext>()
            .UseNexusStackPostgres(ConnectionString, FilesDbContext.SchemaName).Options), timeout.Token);
        await MigrateContextAsync(new AuditingDbContext(new DbContextOptionsBuilder<AuditingDbContext>()
            .UseNexusStackPostgres(ConnectionString, AuditingDbContext.SchemaName).Options), timeout.Token);
        await MigrateContextAsync(new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNexusStackPostgres(ConnectionString, SchedulingDbContext.SchemaName).Options), timeout.Token);
        await OperationJournalDatabase.MigrateAsync(ConnectionString, timeout.Token);
    }

    private static async Task MigrateContextAsync(DbContext context, CancellationToken cancellationToken)
    {
        await using (context)
        {
            await context.Database.MigrateAsync(cancellationToken);
        }
    }

    public async Task MigrateThroughCliAsync()
    {
        var result = await RunMigrationAsync(ConnectionString);
        // 子进程输出可能包含框架异常，失败时也不把凭据写进测试日志。
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Identity migrations applied.", result.Output, StringComparison.Ordinal);
        var platform = await RunMigrationAsync(ConnectionString, "Platform");
        var diagnostic = System.Text.RegularExpressions.Regex.Match(platform.Error, @"kind=[A-Za-z0-9]+; cause=[A-Za-z0-9]+").Value;
        Assert.True(platform.ExitCode == 0, $"Platform migration exit={platform.ExitCode}; {diagnostic}");
        Assert.Contains("Platform migrations applied.", platform.Output, StringComparison.Ordinal);
        var files = await RunMigrationAsync(ConnectionString, "Files");
        Assert.Equal(0, files.ExitCode);
        Assert.Contains("Files migrations applied.", files.Output, StringComparison.Ordinal);
        var auditing = await RunMigrationAsync(ConnectionString, "Auditing");
        Assert.Equal(0, auditing.ExitCode);
        Assert.Contains("Auditing migrations applied.", auditing.Output, StringComparison.Ordinal);
        var scheduling = await RunMigrationAsync(ConnectionString, "Scheduling");
        Assert.Equal(0, scheduling.ExitCode);
        Assert.Contains("Scheduling migrations applied.", scheduling.Output, StringComparison.Ordinal);
        var journal = await RunMigrationAsync(ConnectionString, "OperationJournal");
        Assert.Equal(0, journal.ExitCode);
        Assert.Contains("OperationJournal migrations applied.", journal.Output, StringComparison.Ordinal);
    }

    internal static async Task<(int ExitCode, string Output, string Error)> RunMigrationAsync(string? connectionString, string context = "Identity")
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(PlatformHostMarker).Assembly.Location);
        start.ArgumentList.Add(context == "OperationJournal" ? "migrate-operation-journal" : "migrate-" + context.ToLowerInvariant());
        start.Environment["ConnectionStrings__" + context] = connectionString ?? string.Empty;
        start.Environment["AgileConfig__AppId"] = "migration-probe";
        start.Environment["AgileConfig__Secret"] = "unused-test-secret";
        start.Environment["AgileConfig__Nodes"] = "http://127.0.0.1:1";
        start.Environment["RabbitMQ__HostName"] = "127.0.0.1";
        start.Environment["RabbitMQ__Port"] = "1";
        start.Environment["Identity__Root__UserName"] = "x";
        start.Environment["Identity__Root__Password"] = "unused-test-password";
        start.Environment["Jwt__SigningKey"] = string.Empty;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        return (process.ExitCode, await output, await errors);
    }

    public async ValueTask DisposeAsync()
    {
        using var pool = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(pool);
        await ExecuteAdminAsync($"DROP DATABASE \"{_name}\" WITH (FORCE)");
    }

    public async Task SetAvailableAsync(bool available)
    {
        await ExecuteAdminAsync($"ALTER DATABASE \"{_name}\" ALLOW_CONNECTIONS {(available ? "true" : "false")}");
        if (!available)
        {
            await ExecuteAdminAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_name}'");
        }
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var operation = await JourneyDatabaseOperation.EnterAsync(preparation: sql.StartsWith("CREATE DATABASE", StringComparison.Ordinal));
        // Administrative commands are infrequent; do not reuse an idle management socket.
        // Keep ConnectionString unchanged so the actual hosts still exercise business pools.
        var admin = new NpgsqlConnectionStringBuilder(_admin) { Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
