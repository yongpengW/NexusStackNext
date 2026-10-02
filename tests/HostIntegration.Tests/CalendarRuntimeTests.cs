using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Endpoints;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CalendarRuntimeTests
{
    [Fact]
    public async Task CalendarRuntime_ProbesAvailableIanaData()
    {
        var unavailable = Environment.GetEnvironmentVariable("NSN_CALENDAR_ENVIRONMENT_PROBE") == "unavailable";
        using var host = new HostBuilder().UseEnvironment("Testing").ConfigureServices((context, services) =>
        {
            services.AddHealthChecks();
            services.AddSchedulingModule(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduling:Storage:Provider"] = "Memory",
                ["Scheduling:Worker:Enabled"] = "false",
            }).Build(), context.HostingEnvironment);
        }).Build();
        var calendar = host.Services.GetRequiredService<IScheduleCalendar>();
        // 子进程真的失去 IANA 数据能力，不能把一个替身的报错当成部署故障。
        Assert.Equal(!unavailable, calendar.Normalize(new ScheduleRuleInput("Cron", "0 0 * * *", "Asia/Shanghai")).IsSuccess);
        var health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(unavailable ? HealthStatus.Unhealthy : HealthStatus.Healthy, health.Status);
        Assert.Contains("scheduling-calendar", health.Entries.Keys);
        try
        {
            if (unavailable)
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
                Assert.Contains("scheduling.timezone.unavailable", failure.Message, StringComparison.Ordinal);
            }
            else { await host.StartAsync(); }
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task MissingIanaData_InARealProcess_FailsReadinessAndStartup()
    {
        var emptyZones = Path.Combine(Path.GetTempPath(), "nsn-empty-timezones-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyZones);
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(CalendarRuntimeTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=NexusStackNext.HostIntegration.Tests.CalendarRuntimeTests.CalendarRuntime_ProbesAvailableIanaData");
            var reports = Path.Combine(Path.GetTempPath(), "nsn-calendar-environment-" + Guid.NewGuid().ToString("N"));
            start.ArgumentList.Add("/Logger:trx;LogFileName=probe.trx");
            start.ArgumentList.Add("/ResultsDirectory:" + reports);
            start.Environment["NSN_CALENDAR_ENVIRONMENT_PROBE"] = "unavailable";
            // NLS 禁止 Windows 的 IANA 转换；Unix 改为真实的空 tzdata 目录。
            if (OperatingSystem.IsWindows()) { start.Environment["DOTNET_SYSTEM_GLOBALIZATION_USENLS"] = "1"; }
            else { start.Environment["TZDIR"] = emptyZones; }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            var diagnostic = Path.Combine(Path.GetTempPath(), "nsn-calendar-environment-" + Guid.NewGuid().ToString("N") + ".log");
            await File.WriteAllTextAsync(diagnostic, (await output) + Environment.NewLine + (await errors));
            Assert.True(process.ExitCode == 0, $"独立时区故障探针失败，退出码 {process.ExitCode}；私有诊断：{diagnostic}");
            var counters = XDocument.Load(Path.Combine(reports, "probe.trx")).Descendants().Single(element => element.Name.LocalName == "Counters");
            Assert.Equal("1", (string?)counters.Attribute("total"));
            Assert.Equal("1", (string?)counters.Attribute("passed"));
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOTNET_ENVIRONMENT"] = "Testing",
                ["Identity__Storage__Provider"] = "Memory",
                ["Platform__Storage__Provider"] = "Memory",
                ["Files__Storage__Provider"] = "Memory",
                ["Auditing__Storage__Provider"] = "Memory",
                ["OperationJournal__Storage__Provider"] = "Memory",
                ["Scheduling__Storage__Provider"] = "Memory",
                ["Scheduling__Worker__Enabled"] = "false",
                [OperatingSystem.IsWindows() ? "DOTNET_SYSTEM_GLOBALIZATION_USENLS" : "TZDIR"] = OperatingSystem.IsWindows() ? "1" : emptyZones,
            };
            var startupFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var unexpected = await PlatformHostProcess.StartAsync("unused", settings: settings);
            });
            var startupLog = Assert.IsType<string>(startupFailure.Data["DiagnosticPath"]);
            Assert.True((await File.ReadAllTextAsync(startupLog)).Contains("scheduling.timezone.unavailable", StringComparison.Ordinal),
                "真实宿主应因 IANA 时区能力缺失拒绝启动；不回显私有诊断。");
        }
        finally { Directory.Delete(emptyZones); }
    }
}
