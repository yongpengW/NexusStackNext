using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SchedulingDefinitionTests
{
    [Theory]
    [InlineData("/api/scheduling/tasks/?page=1001&limit=1")]
    [InlineData("/api/scheduling/tasks/1/occurrences?page=1001&limit=1")]
    [InlineData("/api/scheduling/tasks/1/occurrences?limit=101")]
    public async Task InvestigationQueries_RejectUnboundedPages(string path)
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PauseAndResume_RejectStaleVersions_AndRepeatedPauseIsANoOp()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
            new { code = "conditional-plan", intervalSeconds = 30, firstRunInSeconds = 3600, targetKind = "costing.recalculate", targetId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").GetInt64();
        using var stale = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var pause = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NoContent, pause.StatusCode);
        using var repeat = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var paused = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.False(paused.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(2, paused.GetProperty("version").GetInt64());
        using var staleResume = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/resume", UriKind.Relative), new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, staleResume.StatusCode);
        using var resume = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/resume", UriKind.Relative), new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.NoContent, resume.StatusCode);
        page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var resumed = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.True(resumed.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(3, resumed.GetProperty("version").GetInt64());
    }

    [Theory]
    [InlineData(0, 0, "scheduling.interval.invalid")]
    [InlineData(0.5, 0, "scheduling.interval.invalid")]
    [InlineData(31622401, 0, "scheduling.interval.invalid")]
    [InlineData(1e100, 0, "scheduling.interval.invalid")]
    [InlineData(30, -1, "scheduling.first_run.invalid")]
    [InlineData(30, 1e100, "scheduling.first_run.invalid")]
    public async Task Definition_RejectsUnboundedTimeWithoutThrowing(double intervalSeconds, double firstRunInSeconds, string errorCode)
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var invalid = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
            new { code = "invalid-time", intervalSeconds, firstRunInSeconds, targetKind = "costing.recalculate", targetId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(errorCode, (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Empty(page.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Definition_RequiresAKnownTarget_AndPreservesTheBinding()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var itemId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        foreach (var target in new[]
        {
            new { kind = (string?)null, id = itemId },
            new { kind = (string?)"arbitrary.http", id = itemId },
            new { kind = (string?)"costing.recalculate", id = Guid.Empty },
        })
        {
            using var invalid = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
                new { code = "invalid-target", intervalSeconds = 30, firstRunInSeconds = 3600, targetKind = target.kind, targetId = target.id });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("scheduling.target.invalid", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
            new { code = "daily-cost", intervalSeconds = 30, firstRunInSeconds = 3600, targetKind = "costing.recalculate", targetId = itemId, actorId = -1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var plan = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal("costing.recalculate", plan.GetProperty("targetKind").GetString());
        Assert.Equal(itemId, plan.GetProperty("targetId").GetGuid());
        Assert.Equal(new JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter).Subject,
            plan.GetProperty("createdBy").GetString());
        Assert.Equal(1, plan.GetProperty("version").GetInt64());
    }
}
