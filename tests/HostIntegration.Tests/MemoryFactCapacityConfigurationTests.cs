using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFactCapacityConfigurationTests
{
    public static TheoryData<string, string, string> InvalidPolicies => new()
    {
        { "Platform", "MaxRecords", "0" },
        { "Identity", "MaxRecords", "0" },
        { "Files", "MaxRecords", "0" },
        { "Scheduling", "MaxRecords", "0" },
        { "Platform", "MaxPayloadBytes", "0" },
        { "Identity", "MaxPayloadBytes", "0" },
        { "Files", "MaxPayloadBytes", "0" },
        { "Scheduling", "MaxPayloadBytes", "0" },
        { "Platform", "MaxRecordPayloadBytes", "0" },
        { "Identity", "MaxRecordPayloadBytes", "0" },
        { "Files", "MaxRecordPayloadBytes", "0" },
        { "Scheduling", "MaxRecordPayloadBytes", "0" },
        { "Platform", "MaxPayloadBytes", "3" },
        { "Identity", "MaxPayloadBytes", "3" },
        { "Files", "MaxPayloadBytes", "3" },
        { "Scheduling", "MaxPayloadBytes", "3" },
    };

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public async Task Host_RejectsInvalidMemoryFactPolicy_InsteadOfStartingWithoutABound(string context, string key, string value)
    {
        await using var app = new InvalidCapacityApp(context, key, value) { SchedulingWorkerEnabled = false };
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("内存事实容量策略超出允许范围", error.Message, StringComparison.Ordinal);
    }

    private sealed class InvalidCapacityApp(string context, string key, string value) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{context}:AuditDelivery:MemoryCapacity:{key}"] = value,
            }));
            return base.CreateHost(builder);
        }
    }
}
