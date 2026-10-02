using System.Net;
using System.Net.Sockets;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationNewHostStartupTests
{
    [Theory]
    [InlineData("Gateway", "Memory")]
    [InlineData("Costing", "Memory")]
    [InlineData("Gateway", "Postgres")]
    [InlineData("Costing", "Postgres")]
    public async Task ProductionJournalMisconfiguration_StopsNewHostsBeforeAnyDatabaseConnection(string context, string provider)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)probe.LocalEndpoint).Port,
            Database = "startup-probe",
            Username = "startup-probe",
            Timeout = 1,
        }.ConnectionString;
        var assembly = context == "Gateway" ? typeof(GatewayHostMarker).Assembly.Location : typeof(CostingHostMarker).Assembly.Location;
        var start = BusinessProcess.StartInfo(assembly, context, connection);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["Gateway__RouteTablePath"] = Path.Combine(AppContext.BaseDirectory, "routes.json");
        start.Environment["OperationJournal__Storage__Provider"] = provider;
        start.Environment["ConnectionStrings__OperationJournal"] = string.Empty;
        start.Environment["RabbitMQ__HostName"] = string.Empty;
        var result = await BusinessProcess.RunToExitAsync(start);
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(result.Output.Contains("OperationJournal", StringComparison.Ordinal), "Missing journal configuration diagnostic.");
        Assert.False(probe.Pending(), "Invalid journal configuration must fail before opening a business database connection.");
    }
}
