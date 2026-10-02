using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexusStackNext.PlatformHost;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationJournalStorageStartupTests
{
    [Theory]
    [InlineData(null, null, "必须配置 ConnectionStrings:OperationJournal")]
    [InlineData("Postgres", " ", "必须配置 ConnectionStrings:OperationJournal")]
    [InlineData("Memory", null, "OperationJournal:Storage:Provider=Memory 仅允许")]
    [InlineData("Typo", null, "OperationJournal:Storage:Provider 仅支持")]
    public async Task InvalidProductionJournalStorage_RefusesToStartBeforeConnectingToAnyDatabase(
        string? provider, string? journalConnection, string expected)
    {
        using var databaseProbe = new TcpListener(IPAddress.Loopback, 0);
        databaseProbe.Start();
        var port = ((IPEndPoint)databaseProbe.LocalEndpoint).Port;
        var databaseConnection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "startup-probe",
            Username = "startup-probe",
            Timeout = 1,
        }.ConnectionString;
        await using var app = new StorageApp(provider, journalConnection, databaseConnection);

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains(expected, error.ToString(), StringComparison.Ordinal);
        Assert.False(databaseProbe.Pending(), "宿主应先拒绝 journal 配置，再尝试连接任何数据库。");
    }

    private sealed class StorageApp(string? provider, string? journalConnection, string databaseConnection)
        : WebApplicationFactory<PlatformHostMarker>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AgileConfig:AppId"] = string.Empty,
                ["AgileConfig:Nodes"] = string.Empty,
                ["RabbitMQ:HostName"] = string.Empty,
                ["Identity:Storage:Provider"] = "Postgres",
                ["ConnectionStrings:Identity"] = databaseConnection,
                ["Platform:Storage:Provider"] = "Postgres",
                ["ConnectionStrings:Platform"] = databaseConnection,
                ["Scheduling:Storage:Provider"] = "Postgres",
                ["ConnectionStrings:Scheduling"] = databaseConnection,
                ["Auditing:Storage:Provider"] = "Postgres",
                ["ConnectionStrings:Auditing"] = databaseConnection,
                ["Files:Storage:Provider"] = "Postgres",
                ["ConnectionStrings:Files"] = databaseConnection,
                ["OperationJournal:Storage:Provider"] = provider,
                ["ConnectionStrings:OperationJournal"] = journalConnection,
                ["OperationJournal:WriteTimeout"] = "00:00:02",
            };
            // 未指定 provider 与显式空值不同；默认 Postgres 的用例真正省略该配置键。
            if (provider is null) { settings.Remove("OperationJournal:Storage:Provider"); }
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(Environments.Production);
    }
}
