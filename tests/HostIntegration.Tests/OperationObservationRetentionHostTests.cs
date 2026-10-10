using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationObservationRetentionHostTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_AppliesConfiguredRetention_AndCapacityReflectsCommittedCleanup(bool enabled)
    {
        await using var app = new RetentionApp(enabled);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var now = DateTimeOffset.UtcNow;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
            foreach (var received in new[] { now.AddDays(-2), now })
            {
                var observation = OperationObservation.Record(new OperationObservationId(Guid.NewGuid()),
                    new OperationObservationData(new OperationId(Guid.NewGuid()), "platform", "http", "finished", "completed",
                        now.AddDays(-2), "actor", "trace", "GET", "/api/example", 200, 1), received).Value;
                Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(observation)).Value);
            }
        }
        if (enabled)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                using var response = await client.GetAsync(new Uri("/api/auditing/capacity", UriKind.Relative));
                var data = await response.Content.ReadApiDataAsync();
                if (data.GetProperty("observations").GetProperty("records").ReadHttpInt64() == 1) { break; }
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
        else { await Task.Delay(TimeSpan.FromMilliseconds(1500)); }
        using var capacity = await client.GetAsync(new Uri("/api/auditing/capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, capacity.StatusCode);
        var found = await capacity.Content.ReadApiDataAsync();
        Assert.Equal(enabled ? 1 : 2, found.GetProperty("observations").GetProperty("records").ReadHttpInt64());
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [PostgresFact]
    public async Task RestartedPostgresHost_CleansPreviouslyReceivedObservations_AndKeepsRecentOnes()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using (var first = new PersistentRetentionApp(database.ConnectionString, false))
        {
            using var client = first.CreateClient();
            await using var scope = first.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
            var now = DateTimeOffset.UtcNow;
            foreach (var received in new[] { now.AddDays(-2), now })
            {
                var observation = OperationObservation.Record(new OperationObservationId(Guid.NewGuid()),
                    new OperationObservationData(new OperationId(Guid.NewGuid()), "platform", "http", "finished", "completed",
                        now.AddDays(-2), "actor", "trace", "GET", "/api/example", 200, 1), received).Value;
                Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(observation)).Value);
            }
        }
        await using var restarted = new PersistentRetentionApp(database.ConnectionString, true);
        using var after = restarted.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(after, "journey-root", PlatformAppWithRootAccount.RootPassword);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var response = await after.GetAsync(new Uri("/api/auditing/capacity", UriKind.Relative));
            var data = await response.Content.ReadApiDataAsync();
            if (data.GetProperty("observations").GetProperty("records").ReadHttpInt64() == 1) { break; }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        using var capacity = await after.GetAsync(new Uri("/api/auditing/capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, capacity.StatusCode);
        var found = await capacity.Content.ReadApiDataAsync();
        Assert.Equal(1, found.GetProperty("observations").GetProperty("records").ReadHttpInt64());
    }

    [Theory]
    [InlineData("Auditing:Retention:ObservationRetention", "00:00:00")]
    [InlineData("Auditing:Retention:Interval", "00:00:00")]
    [InlineData("Auditing:Retention:BatchSize", "0")]
    public async Task InvalidRetentionConfiguration_RefusesToStart(string key, string value)
    {
        await using var app = new RetentionApp(true, key, value);
        var failure = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("Auditing:Retention", failure.ToString(), StringComparison.Ordinal);
    }

    private sealed class RetentionApp(bool enabled, string? invalidKey = null, string? invalidValue = null) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var values = new Dictionary<string, string?>
            {
                ["Identity:Root:UserName"] = PlatformAppWithRootAccount.RootUserName,
                ["Identity:Root:Password"] = PlatformAppWithRootAccount.RootPassword,
                ["Auditing:Retention:Enabled"] = enabled.ToString(),
                ["Auditing:Retention:ObservationRetention"] = "1.00:00:00",
                ["Auditing:Retention:Interval"] = "00:00:01",
                ["Auditing:Retention:BatchSize"] = "1",
            };
            if (invalidKey is not null) { values[invalidKey] = invalidValue; }
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(values));
            return base.CreateHost(builder);
        }
    }

    private sealed class PersistentRetentionApp(string connection, bool enabled) : PersistentIdentityApp(connection,
        PlatformAppWithRootAccount.RootPassword, schedulingWorkerEnabled: false)
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auditing:Retention:Enabled"] = enabled.ToString(),
                ["Auditing:Retention:ObservationRetention"] = "1.00:00:00",
                ["Auditing:Retention:Interval"] = "00:00:01",
                ["Auditing:Retention:BatchSize"] = "1",
            }));
            return base.CreateHost(builder);
        }
    }
}
