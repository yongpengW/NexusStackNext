using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class HttpInt64ContractTests
{
    [Fact]
    public async Task HostHttpOptions_DoNotChangeTheStoredOutboxMessageProtocol()
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        var serializer = app.Services.GetRequiredService<IIntegrationEventSerializer>();
        var message = new ScheduleTriggeredV1
        {
            EventId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.Parse("2026-10-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ScheduledAt = DateTimeOffset.Parse("2026-10-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            PlanId = 9007199254740993,
            TriggerSequence = long.MaxValue,
            TargetKind = "costing.recalculate",
            TargetId = Guid.NewGuid(),
            CreatedBy = "42",
        };
        var outbox = OutboxEntry.From(message, serializer);
        var stored = JsonSerializer.Deserialize<JsonElement>(outbox.ToEnvelope().Payload);
        Assert.Equal(JsonValueKind.Number, stored.GetProperty("planId").ValueKind);
        Assert.Equal(9007199254740993, stored.GetProperty("planId").GetInt64());
        Assert.Equal(long.MaxValue, stored.GetProperty("triggerSequence").GetInt64());
        Assert.Equal(message, serializer.Deserialize<ScheduleTriggeredV1>(outbox.ToEnvelope().Payload));
    }

    [Fact]
    public async Task ExactNumericAndStringInputs_AreCompatible_IncludingNullableValuesAndDictionaryKeys()
    {
        await using var app = CreateProbe();
        app.MapPost("/echo", (Int64Input input, ApiResponses responses) => responses.Ok(input));
        app.MapPost("/map", (Dictionary<long, long?> input, ApiResponses responses) => responses.Ok(input));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var (token, expected) in new[]
        {
            ("9007199254740993", "9007199254740993"), ("9223372036854775807", "9223372036854775807"),
            ("-9223372036854775808", "-9223372036854775808"), ("\"+01\"", "1"), ("\"-00\"", "0"),
        })
        {
            using var content = new StringContent($$"""{"id":{{token}},"values":[{{token}}],"parentId":{{token}}}""", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri("/echo", UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = await response.Content.ReadApiDataAsync();
            Assert.Equal(expected, data.GetProperty("id").GetString());
            Assert.Equal(expected, data.GetProperty("parentId").GetString());
            Assert.Equal(expected, Assert.Single(data.GetProperty("values").EnumerateArray()).GetString());
        }
        using var mapContent = new StringContent("""{"9223372036854775807":"-9223372036854775808","+01":null}""", Encoding.UTF8, "application/json");
        using var mapResponse = await client.PostAsync(new Uri("/map", UriKind.Relative), mapContent);
        Assert.Equal(HttpStatusCode.OK, mapResponse.StatusCode);
        var map = await mapResponse.Content.ReadApiDataAsync();
        Assert.Equal("-9223372036854775808", map.GetProperty("9223372036854775807").GetString());
        Assert.Equal(JsonValueKind.Null, map.GetProperty("1").ValueKind);
    }

    [Fact]
    public async Task JavaScript_UsesActualGatewayPlanIdAndVersion_ToPauseTheOriginalPlan()
    {
        await using var platform = new PlatformAppWithRootAccount();
        platform.UseKestrel(0);
        using var direct = platform.CreateClient();
        await using var gateway = new GatewayHttpApp(direct.BaseAddress!.AbsoluteUri)
        {
            SigningKey = "integration-test-signing-key-long-enough-for-hs256",
            RateLimitPermitLimit = 30,
        };
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.json"))).Value;
        gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend" }));
        using var client = gateway.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
            new { code = "javascript-plan", intervalSeconds = 30, firstRunInSeconds = 3600, targetKind = "costing.recalculate", targetId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var originalId = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").GetString();
        var page = await client.GetStringAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var request = JsonSerializer.Deserialize<JsonElement>(await JavaScriptAsync(page, """
            const assert = require('node:assert/strict');
            const page = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));
            assert.equal(page.total, '1');
            assert.equal(page.totalPage, '1');
            assert.equal(page.page, 1);
            const plan = page.data[0];
            assert.equal(typeof plan.taskId, 'string');
            assert.ok(BigInt(plan.taskId) > 9007199254740992n);
            assert.equal(plan.version, '1');
            process.stdout.write(JSON.stringify({path: `/api/scheduling/tasks/${plan.taskId}/pause`, body: {expectedVersion: plan.version}}));
            """));
        var path = new Uri(request.GetProperty("path").GetString()!, UriKind.Relative);
        using var content = new StringContent(request.GetProperty("body").GetRawText(), Encoding.UTF8, "application/json");
        using var paused = await client.PostAsync(path, content);
        Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
        using var staleContent = new StringContent(request.GetProperty("body").GetRawText(), Encoding.UTF8, "application/json");
        using var stale = await client.PostAsync(path, staleContent);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var result = await client.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var plan = Assert.Single((await result.Content.ReadApiDataAsync()).EnumerateArray());
        Assert.Equal(originalId, plan.GetProperty("taskId").GetString());
        Assert.Equal("2", plan.GetProperty("version").GetString());
        Assert.False(plan.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task InvalidInt64Input_ReturnsProblemDetails_WithoutExecutingTheRequest()
    {
        await using var app = CreateProbe();
        var accepted = new List<long>();
        app.MapPost("/values", (Int64Input input, ApiResponses responses) =>
        {
            accepted.Add(input.Id);
            return responses.Ok(input);
        });
        app.MapGet("/values", (ApiResponses responses) => responses.Ok(accepted));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var value in new[] { "9223372036854775808", "-9223372036854775809", "1.0", "1e3", "true", "null", "{}", "[]",
            "\"9223372036854775808\"", "\"-9223372036854775809\"", "\"\"", "\" \"", "\" 1\"", "\"1 \"", "\"1.0\"", "\"1e3\"", "\"1\\u0000\"" })
        {
            using var content = new StringContent($$"""{"id":{{value}},"values":[],"parentId":null}""", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri("/values", UriKind.Relative), content);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Unexpected status for {value}: {response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("http.400", problem.GetProperty("errorCode").GetString());
            Assert.Equal(JsonValueKind.String, problem.GetProperty("timestamp").ValueKind);
        }
        using var read = await client.GetAsync(new Uri("/values", UriKind.Relative));
        Assert.Empty((await read.Content.ReadApiDataAsync()).EnumerateArray());
    }

    [Fact]
    public async Task JavaScript_CanRoundTripAllInt64Values_WithoutChangingOtherJsonTypes()
    {
        await using var app = CreateProbe();
        app.MapGet("/values", (ApiResponses responses) => responses.Ok(new
        {
            id = 9007199254740993L,
            nested = new { version = 1L },
            values = new[] { long.MinValue, 0, long.MaxValue },
            nullableValues = new long?[] { null, 9007199254740991, 9007199254740992 },
            amount = 1.25m,
            count = 7,
            interval = 1.5,
            taskId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        }));
        app.MapPost("/echo", (Int64Input input, ApiResponses responses) => responses.Ok(input));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var raw = await client.GetStringAsync(new Uri("/values", UriKind.Relative));
        var returned = await JavaScriptAsync(raw, """
            const assert = require('node:assert/strict');
            const body = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));
            assert.equal(typeof body.timestamp, 'string');
            assert.equal(body.code, 200);
            const d = body.data;
            assert.equal(d.id, '9007199254740993');
            assert.equal(d.nested.version, '1');
            assert.deepEqual(d.values, ['-9223372036854775808', '0', '9223372036854775807']);
            assert.deepEqual(d.nullableValues, [null, '9007199254740991', '9007199254740992']);
            assert.equal(d.amount, 1.25);
            assert.equal(d.count, 7);
            assert.equal(d.interval, 1.5);
            assert.equal(d.taskId, '00000000-0000-0000-0000-000000000001');
            process.stdout.write(JSON.stringify({id: d.id, values: d.values, parentId: null}));
            """);
        using var content = new StringContent(returned, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/echo", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadApiDataAsync();
        Assert.Equal("9007199254740993", data.GetProperty("id").GetString());
        Assert.Equal("9223372036854775807", data.GetProperty("values")[2].GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("parentId").ValueKind);
    }

    private static WebApplication CreateProbe()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddApiResponseContract();
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseApiResponseContract();
        return app;
    }

    private static async Task<string> JavaScriptAsync(string input, string script)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--eval", script },
            },
        };
        Assert.True(process.Start(), "Node.js is required for the browser JSON contract tests.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    public sealed record Int64Input(long Id, long[] Values, long? ParentId);
}
