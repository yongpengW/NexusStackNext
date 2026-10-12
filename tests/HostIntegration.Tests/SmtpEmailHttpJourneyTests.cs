using System.Net;
using System.Net.Http.Json;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Notifications.Application;
using NexusStackNext.Notifications.Infrastructure;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SmtpEmailHttpJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Enabled_module_requires_explicit_migration_and_production_tls()
    {
        await using var database = await databases.CreateAsync();
        var settings = new Dictionary<string, string>
        {
            ["Notifications__Enabled"] = "true",
            ["ConnectionStrings__Notifications"] = database.ConnectionString,
            ["Notifications__Smtp__Host"] = "127.0.0.1",
            ["Notifications__Smtp__Sender"] = "sender@example.test",
            ["Notifications__Worker__Enabled"] = "false",
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => PlatformHostProcess.StartAsync(database.ConnectionString, settings: settings));
        Assert.Equal(1, (await IdentityJourneyDatabase.RunMigrationAsync(null, "Notifications")).ExitCode);
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Notifications")).ExitCode);
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Notifications")).ExitCode);
        settings["Notifications__Smtp__Security"] = "None";
        await Assert.ThrowsAsync<InvalidOperationException>(() => PlatformHostProcess.StartAsync(database.ConnectionString, settings: settings));
        settings["Notifications__Smtp__Security"] = "StartTls";
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, settings: settings);
        using var ready = await host.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [PostgresFact]
    public async Task Process_crash_after_smtp_data_keeps_unknown_and_restart_does_not_resend()
    {
        await using var receiver = new TestSmtpReceiver { Outcome = "hold" };
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true)) { await NotificationsDatabase.MigrateAsync(database.ConnectionString); }
        var settings = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Testing",
            ["Notifications__Enabled"] = "true",
            ["ConnectionStrings__Notifications"] = database.ConnectionString,
            ["Notifications__Smtp__Host"] = "127.0.0.1",
            ["Notifications__Smtp__Sender"] = "sender@example.test",
            ["Notifications__Smtp__Port"] = receiver.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Notifications__Smtp__Security"] = "None",
        };
        var id = Guid.NewGuid();
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "smtp-journey-root-password", settings: settings))
        {
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "smtp-journey-root-password");
            using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/notifications/emails", UriKind.Relative), new SubmitEmail(id, ["recipient@example.test"], "report", "private body", null, []));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            await receiver.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.CrashAsync();
        }
        await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "smtp-journey-root-password", settings: settings);
        await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "smtp-journey-root-password");
        using var read = await resumed.Client.GetAsync(new Uri($"/api/notifications/emails/{id:D}", UriKind.Relative));
        var status = await read.Content.ReadApiDataAsync();
        Assert.Equal("Unknown", status.GetProperty("state").GetString());
        Assert.Equal(1, status.GetProperty("attempt").GetInt32());
        using var replay = await resumed.Client.PostAsJsonAsync(new Uri("/api/notifications/emails", UriKind.Relative), new SubmitEmail(id, ["recipient@example.test"], "report", "private body", null, []));
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("Unknown", (await replay.Content.ReadApiDataAsync()).GetProperty("state").GetString());
        Assert.Single(receiver.Messages);
    }

    [PostgresFact]
    public async Task Email_http_enforces_permissions_and_keeps_the_original_intent_after_process_restart()
    {
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true)) { await NotificationsDatabase.MigrateAsync(database.ConnectionString); }
        var settings = new Dictionary<string, string>
        {
            ["Notifications__Enabled"] = "true",
            ["ConnectionStrings__Notifications"] = database.ConnectionString,
            ["Notifications__Smtp__Host"] = "127.0.0.1",
            ["Notifications__Smtp__Sender"] = "sender@example.test",
            ["Notifications__Worker__Enabled"] = "false",
        };
        var id = Guid.NewGuid();
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "smtp-journey-root-password", settings: settings))
        {
            using var anonymous = await host.Client.GetAsync(new Uri($"/api/notifications/emails/{id:D}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var ordinary = new HttpClient { BaseAddress = host.Client.BaseAddress };
            using var registered = await ordinary.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), new { userName = "mail-user", password = "mail-user-test-password" });
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            await PlatformSettingsAccessTests.LoginAsync(ordinary, "mail-user", "mail-user-test-password");
            using var denied = await ordinary.PostAsJsonAsync(new Uri("/api/notifications/emails", UriKind.Relative), new SubmitEmail(id, ["recipient@example.test"], "report", "secret body", null, []));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "smtp-journey-root-password");
            using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/notifications/emails", UriKind.Relative), new SubmitEmail(id, ["recipient@example.test"], "report", "secret body", null, []));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var response = await accepted.Content.ReadAsStringAsync();
            Assert.DoesNotContain("secret body", response, StringComparison.Ordinal);
            Assert.DoesNotContain("recipient@example.test", response, StringComparison.Ordinal);
            using var oversized = await host.Client.PostAsJsonAsync(new Uri("/api/notifications/emails", UriKind.Relative),
                new SubmitEmail(Guid.NewGuid(), ["recipient@example.test"], "report", new string('x', 4 * 1024 * 1024), null, []));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            using var forbiddenRead = await ordinary.GetAsync(new Uri($"/api/notifications/emails/{id:D}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
            using var forbiddenResolve = await ordinary.PostAsJsonAsync(new Uri($"/api/notifications/emails/{id:D}/resolve", UriKind.Relative), new { expectedAttempt = 0, decision = "confirm" });
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResolve.StatusCode);
            using var stale = await host.Client.PostAsJsonAsync(new Uri($"/api/notifications/emails/{id:D}/resolve", UriKind.Relative), new { expectedAttempt = 0, decision = "confirm" });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            await host.CrashAsync();
        }
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "smtp-journey-root-password", settings: settings))
        {
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "smtp-journey-root-password");
            using var read = await host.Client.GetAsync(new Uri($"/api/notifications/emails/{id:D}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var status = await read.Content.ReadApiDataAsync();
            Assert.Equal("Pending", status.GetProperty("state").GetString());
            Assert.Equal(0, status.GetProperty("attempt").GetInt32());
        }
    }
}
