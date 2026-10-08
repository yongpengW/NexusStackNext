using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

// The endpoint inventories use the real composition, without issuing authenticated business requests.
internal sealed class BusinessHostApp<T>(string context, string connection) : WebApplicationFactory<T> where T : class
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{context}"] = connection,
            [$"{context}:Worker:Enabled"] = "false",
            [$"{context}:Messaging:Enabled"] = "false",
            ["OperationJournal:Storage:Provider"] = "Memory",
            ["AgileConfig:AppId"] = string.Empty,
            ["RabbitMQ:HostName"] = string.Empty,
            ["Jwt:SigningKey"] = BusinessProcess.SigningKey,
            ["Jwt:Issuer"] = "nexusstack",
            ["Jwt:Audience"] = "nexusstack",
            ["IdentitySession:BaseAddress"] = "http://127.0.0.1:1/",
        }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
}
