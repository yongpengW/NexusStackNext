using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFactCapacityHttpTests
{
    [Theory]
    [InlineData("platform", "platform.audit_capacity.exhausted")]
    [InlineData("identity", "identity.audit_capacity.exhausted")]
    [InlineData("files", "files.audit_capacity.exhausted")]
    [InlineData("scheduling", "scheduling.audit_capacity_exhausted")]
    public async Task FullCapacity_ReturnsStable503_WithoutChangingCommittedFacts(string context, string errorCode)
    {
        await using var app = new CapacityApp(context) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        if (context != "identity")
        {
            await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        }
        using var accepted = await WriteAsync(client, context, "accepted");
        Assert.Equal(context == "platform" ? HttpStatusCode.NoContent : HttpStatusCode.Created, accepted.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var before = await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        Assert.Equal(context == "files" ? 2 : 1, before.Count);
        using var refused = await WriteAsync(client, context, "must-not-commit");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        var error = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(errorCode, error.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("must-not-commit", error.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));

        switch (context)
        {
            case "platform":
                using (var read = await client.GetAsync(new Uri("/api/platform/settings/capacity.http", UriKind.Relative)))
                {
                    Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                    Assert.Equal("accepted", (await read.Content.ReadApiDataAsync()).GetProperty("value").GetString());
                }
                break;
            case "identity":
                var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                Assert.NotNull(await users.FindByUserNameAsync(UserName.Create("accepted").Value));
                Assert.Null(await users.FindByUserNameAsync(UserName.Create("must-not-commit").Value));
                break;
            case "files":
                var id = (await accepted.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
                using (var deletion = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative)))
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, deletion.StatusCode);
                }
                using (var download = await client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative)))
                {
                    Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                    Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
                }
                Assert.Equal(before, await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
                break;
            case "scheduling":
                var plan = Assert.Single(await scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>().ListAsync());
                Assert.Equal("accepted", plan.Code.Value);
                Assert.Equal(1, plan.Version);
                break;
        }
    }

    private static async Task<HttpResponseMessage> WriteAsync(HttpClient client, string context, string value)
    {
        switch (context)
        {
            case "platform": return await client.PutAsJsonAsync(new Uri("/api/platform/settings/capacity.http", UriKind.Relative), new { value });
            case "identity": return await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), new { userName = value, password = "http-capacity-password" });
            case "files":
                using (var bytes = new ByteArrayContent([1, 2, 3]))
                {
                    return await client.PostAsync(new Uri($"/api/files?name={value}.bin", UriKind.Relative), bytes);
                }
            case "scheduling":
                return await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks", UriKind.Relative), new
                {
                    code = value,
                    intervalSeconds = 3600,
                    firstRunInSeconds = 3600,
                    targetKind = "costing.recalculate",
                    targetId = Guid.NewGuid(),
                });
            default: throw new InvalidOperationException("Unknown capacity test context.");
        }
    }

    private sealed class CapacityApp(string context) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{context}:AuditDelivery:MemoryCapacity:MaxRecords"] = context == "files" ? "2" : "1",
                [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
                ["Identity:Root:UserName"] = context == "identity" ? null : PlatformAppWithRootAccount.RootUserName,
                ["Identity:Root:Password"] = context == "identity" ? null : PlatformAppWithRootAccount.RootPassword,
            }));
            return base.CreateHost(builder);
        }
    }
}
