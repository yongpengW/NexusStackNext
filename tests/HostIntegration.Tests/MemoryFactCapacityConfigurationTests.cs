using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFactCapacityConfigurationTests
{
    [Theory]
    [InlineData("Platform", "00:00:00.050")]
    [InlineData("Identity", "00:00:00.050")]
    [InlineData("Files", "00:00:00.050")]
    [InlineData("Scheduling", "00:00:00.050")]
    [InlineData("Platform", "00:00:30")]
    [InlineData("Identity", "00:00:30")]
    [InlineData("Files", "00:00:30")]
    [InlineData("Scheduling", "00:00:30")]
    public async Task Host_AcceptsBothMemoryWriteBudgetLimits(string context, string value)
    {
        await using var app = new InvalidCapacityApp(context, "Timeout", value, writeBudget: true) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string, string> InvalidWriteBudgets
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var context in new[] { "Platform", "Identity", "Files", "Scheduling" })
            {
                foreach (var value in new[] { "00:00:00", "-00:00:01", "00:00:00.049", "00:00:30.001", "00:00:00.0500001" })
                {
                    cases.Add(context, value);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidWriteBudgets))]
    public async Task Host_RejectsInvalidMemoryWriteBudget_AtComposition(string context, string value)
    {
        await using var app = new InvalidCapacityApp(context, "Timeout", value, writeBudget: true) { SchedulingWorkerEnabled = false };
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("事实容量锁等待预算必须是五十毫秒至三十秒的整毫秒值", error.Message, StringComparison.Ordinal);
    }

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

    private sealed class InvalidCapacityApp(string context, string key, string value, bool writeBudget = false) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{context}:AuditDelivery:{(writeBudget ? "CapacityWrite" : "MemoryCapacity")}:{key}"] = value,
            }));
            return base.CreateHost(builder);
        }
    }
}
