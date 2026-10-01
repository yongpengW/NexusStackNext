using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexusStackNext.PlatformHost;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityStorageStartupTests
{
    [Fact]
    public async Task MigrationWithoutConfiguration_FailsWithConfigurationName()
    {
        var result = await IdentityJourneyDatabase.RunMigrationAsync(null);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Identity migration requires ConnectionStrings__Identity in the environment.", result.Error.Trim());
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task MigrationWithoutReachableDatabase_FailsWithSanitizedDiagnostic()
    {
        var result = await IdentityJourneyDatabase.RunMigrationAsync(
            "Host=127.0.0.1;Port=1;Database=unavailable;Timeout=1");
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Error.Trim() == "Identity migration failed. Check database access and migration compatibility.",
            "迁移失败必须返回受控诊断，不能输出底层异常。");
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("Memory", "仅允许 Development / Testing")]
    [InlineData("Typo", "仅支持 Postgres / Memory")]
    public async Task InvalidProductionStorageMode_RefusesToStart(string provider, string expected)
    {
        await using var app = new StorageApp(provider);
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains(expected, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionWithoutDatabaseConfiguration_RefusesToStart()
    {
        await using var app = new StorageApp(null);
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("ConnectionStrings:Identity", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class StorageApp(string? provider) : WebApplicationFactory<PlatformHostMarker>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Identity:Storage:Provider"] = provider,
                    ["ConnectionStrings:Identity"] = null,
                }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Jwt:SigningKey"] = "integration-test-signing-key-long-enough-for-hs256",
                }));
        }
    }
}
