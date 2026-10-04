using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

internal sealed class MemoryBudgetApp(PausingClock clock, string context = "Platform", bool root = false, string? timeout = "00:00:00.150",
    IIntegrationEventSerializer? serializer = null) : PlatformApp
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Identity:Root:UserName"] = root ? PlatformAppWithRootAccount.RootUserName : null,
            ["Identity:Root:Password"] = root ? PlatformAppWithRootAccount.RootPassword : null,
        };
        if (timeout is not null) { settings[$"{context}:AuditDelivery:CapacityWrite:Timeout"] = timeout; }
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IClock>(clock);
            if (serializer is not null) { services.AddSingleton(serializer); }
        });
    }
}
