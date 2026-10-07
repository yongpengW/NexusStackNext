using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityWriteConfigurationTests
{
    [PostgresFact]
    public async Task PlatformModules_RejectUnboundedOrNonMillisecondBudgets_WhileAcceptingTheLowerBoundary()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        foreach (var (owner, invalid) in new[]
        {
            ("Platform", "00:00:00"), ("Identity", "00:00:00.049"), ("Files", "00:00:30.001"), ("Scheduling", "00:00:00.0500001"),
        })
        {
            await using (var valid = new ConfigurationApp(database.ConnectionString, owner, "00:00:00.050"))
            {
                using var client = valid.CreateClient();
                using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            }
            await using var rejected = new ConfigurationApp(database.ConnectionString, owner, invalid);
            var error = Assert.Throws<InvalidOperationException>(() => rejected.CreateClient());
            Assert.Contains("事实容量锁等待预算必须是五十毫秒至三十秒的整毫秒值", error.Message, StringComparison.Ordinal);
        }
    }

    [PostgresFact]
    public async Task BusinessHosts_RejectInvalidWriteBudgets_WhileAcceptingTheUpperBoundary()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await JourneyDatabaseOperation.RunAsync(() => CostingDatabase.MigrateAsync(database.ConnectionString));
        await JourneyDatabaseOperation.RunAsync(() => PricingDatabase.MigrateAsync(database.ConnectionString));
        foreach (var (owner, invalid) in new[] { ("Costing", "-00:00:01"), ("Pricing", "00:00:31") })
        {
            var assembly = owner == "Costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
            var settings = new Dictionary<string, string> { [$"{owner}__AuditDelivery__CapacityWrite__Timeout"] = "00:00:30" };
            await using (var valid = await BusinessProcess.StartAsync(assembly, owner, database.ConnectionString, settings: settings))
            {
                using var live = await valid.Client.GetAsync(new Uri("/health/live", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            }
            var start = BusinessProcess.StartInfo(assembly, owner, database.ConnectionString);
            start.Environment[$"{owner}__AuditDelivery__CapacityWrite__Timeout"] = invalid;
            Assert.Equal(1, (await BusinessProcess.RunToExitAsync(start)).ExitCode);
        }
    }

    private sealed class ConfigurationApp(string connectionString, string owner, string timeout)
        : PersistentIdentityApp(connectionString, schedulingWorkerEnabled: false)
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{owner}:AuditDelivery:CapacityWrite:Timeout"] = timeout,
            }));
            return base.CreateHost(builder);
        }
    }
}
