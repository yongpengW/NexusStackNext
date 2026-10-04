using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NexusStackNext.HostIntegration.Tests;

/// <summary>使用真实模块装配验证单个 Memory 上下文的有限容量。</summary>
internal sealed class MemoryFactCapacityApp(string context, long maxRecords) : PlatformApp
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{context}:AuditDelivery:MemoryCapacity:MaxRecords"] = maxRecords.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
        }));
        return base.CreateHost(builder);
    }
}
