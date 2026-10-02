using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexusStackNext.PlatformHost;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class AuditingStorageStartupTests
{
    [Fact]
    public async Task MigrationWithoutConfiguration_FailsWithConfigurationName()
    {
        var result = await IdentityJourneyDatabase.RunMigrationAsync(null, "Auditing");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Auditing migration requires ConnectionStrings__Auditing in the environment.", result.Error.Trim());
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task MigrationWithoutReachableDatabase_FailsWithSanitizedDiagnostic()
    {
        var result = await IdentityJourneyDatabase.RunMigrationAsync("Host=127.0.0.1;Port=1;Database=unavailable;Timeout=1", "Auditing");
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Error.Trim() == "Auditing migration failed. Check database access and migration compatibility.");
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData(null, "必须配置 ConnectionStrings:Auditing")]
    [InlineData(" ", "必须配置 ConnectionStrings:Auditing")]
    [InlineData("Memory", "Auditing:Storage:Provider=Memory 仅允许")]
    [InlineData("Typo", "Auditing:Storage:Provider 仅支持")]
    public async Task MissingOrInvalidProductionStorage_RefusesToStart(string? provider, string expected)
    {
        await using var app = new StorageApp(provider);
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains(expected, error.ToString(), StringComparison.Ordinal);
    }

    private sealed class StorageApp(string? provider) : WebApplicationFactory<PlatformHostMarker>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Identity:Storage:Provider"] = "Postgres",
                    ["ConnectionStrings:Identity"] = "Host=127.0.0.1;Port=1;Database=unavailable;Timeout=1",
                    ["Platform:Storage:Provider"] = "Postgres",
                    ["ConnectionStrings:Platform"] = "Host=127.0.0.1;Port=1;Database=unavailable;Timeout=1",
                    ["Scheduling:Storage:Provider"] = "Postgres",
                    ["ConnectionStrings:Scheduling"] = "Host=127.0.0.1;Port=1;Database=unavailable;Timeout=1",
                    ["Files:Storage:Provider"] = "Postgres",
                    ["ConnectionStrings:Files"] = "Host=127.0.0.1;Port=1;Database=unavailable;Timeout=1",
                    ["Auditing:Storage:Provider"] = provider,
                    ["ConnectionStrings:Auditing"] = null,
                }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(Environments.Production);
    }
}
