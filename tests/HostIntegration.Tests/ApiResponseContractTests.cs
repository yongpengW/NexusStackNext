using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class ApiResponseContractTests
{
    [Fact]
    public async Task UnsupportedMediaType_UsesProblemContract_AndAllJsonRequestsDocumentIt()
    {
        await using var platform = new PlatformApp();
        using var client = platform.CreateClient();
        using var content = new StringContent("plain input");
        using var rejected = await client.PostAsync(new Uri("/api/identity/login", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
        Assert.Equal("http.415", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

        await using var backend = await GatewayBackend.StartAsync("schema-probe");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var gatewayClient = gateway.CreateClient();
        var checkedRequests = 0;
        foreach (var (source, path) in new[] { (client, "/openapi/v1.json"), (gatewayClient, "/openapi/gateway.json") })
        {
            var document = await source.GetFromJsonAsync<JsonElement>(new Uri(path, UriKind.Relative));
            foreach (var endpoint in document.GetProperty("paths").EnumerateObject())
            {
                foreach (var operation in endpoint.Value.EnumerateObject())
                {
                    if (!operation.Value.TryGetProperty("requestBody", out var request)
                        || !request.GetProperty("content").TryGetProperty("application/json", out _))
                    {
                        continue;
                    }
                    checkedRequests++;
                    Assert.True(operation.Value.GetProperty("responses").TryGetProperty("415", out var unsupported),
                        $"{operation.Name} {endpoint.Name} 缺少 415 响应声明。");
                    var schema = Resolve(unsupported.GetProperty("content").GetProperty("application/problem+json").GetProperty("schema"), document);
                    Assert.True(schema.GetProperty("properties").TryGetProperty("errorCode", out _));
                }
            }
        }
        Assert.True(checkedRequests > 0);
    }

    [Theory]
    [InlineData("", 200)]
    [InlineData("&page=2147483647&limit=200", 200)]
    [InlineData("&limit=0", 400)]
    [InlineData("&limit=201", 400)]
    [InlineData("&page=-1", 400)]
    public async Task EmptyPagination_HonorsDefaultsAndBounds(string query, int expectedStatus)
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        using var response = await client.GetAsync(new Uri($"/api/platform/settings/?scope=empty{query}", UriKind.Relative));
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (expectedStatus == 200)
        {
            Assert.Equal(0, body.GetProperty("total").GetInt64());
            Assert.Equal(0, body.GetProperty("totalPage").GetInt64());
            Assert.Empty(body.GetProperty("data").EnumerateArray());
            if (query.Length == 0)
            {
                Assert.Equal(1, body.GetProperty("page").GetInt32());
                Assert.Equal(50, body.GetProperty("limit").GetInt32());
            }
        }
        else
        {
            Assert.Equal("http.pagination.invalid", body.GetProperty("errorCode").GetString());
        }
    }

    [Fact]
    public async Task TaskPagination_UsesTheSameContract_AndOrdersByStableId()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative),
            new { userName = PlatformAppWithRootAccount.RootUserName, password = PlatformAppWithRootAccount.RootPassword });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadApiDataAsync()).GetProperty("accessToken").GetString());
        long lastId = 0;
        foreach (var code in new[] { "page-a", "page-b" })
        {
            using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
                new { code, intervalSeconds = 30, firstRunInSeconds = 3600 });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            lastId = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").GetInt64();
        }
        var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/?page=2&limit=1", UriKind.Relative));
        Assert.Equal(2, page.GetProperty("total").GetInt64());
        Assert.Equal(lastId, Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("taskId").GetInt64());
        using var invalid = await client.GetAsync(new Uri("/api/scheduling/tasks/?limit=201", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task OpenApi_DescribesWrappedSuccessPageAndProblem_WithoutWrappingTheDocument()
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.True(document.TryGetProperty("openapi", out _));
        Assert.False(document.TryGetProperty("success", out _));
        var paths = document.GetProperty("paths");
        var login = paths.GetProperty("/api/identity/login").GetProperty("post").GetProperty("responses");
        var success = Resolve(login.GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"), document);
        Assert.True(success.GetProperty("properties").TryGetProperty("success", out _));
        var payload = Resolve(success.GetProperty("properties").GetProperty("data"), document);
        Assert.True(payload.TryGetProperty("properties", out var payloadProperties), payload.GetRawText());
        Assert.True(payloadProperties.TryGetProperty("accessToken", out _));
        Assert.True(login.TryGetProperty("400", out _), login.GetRawText());
        var failure = Resolve(login.GetProperty("400").GetProperty("content").GetProperty("application/problem+json").GetProperty("schema"), document);
        Assert.True(failure.GetProperty("properties").TryGetProperty("errorCode", out _));
        var page = Resolve(paths.GetProperty("/api/platform/settings").GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"), document);
        Assert.True(page.GetProperty("properties").TryGetProperty("totalPage", out _));
    }

    private static JsonElement Resolve(JsonElement schema, JsonElement document)
    {
        if (schema.TryGetProperty("oneOf", out var alternatives))
        {
            return Resolve(Assert.Single(alternatives.EnumerateArray(), item =>
                !item.TryGetProperty("type", out var type) || type.GetString() != "null"), document);
        }
        return schema.TryGetProperty("$ref", out var reference)
            ? document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1])
            : schema;
    }

    [Fact]
    public async Task SettingsPagination_ReturnsStableWindowAndTotals_AndRejectsInvalidBounds()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative),
            new { userName = PlatformAppWithRootAccount.RootUserName, password = PlatformAppWithRootAccount.RootPassword });
        var tokens = await login.Content.ReadApiDataAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        foreach (var suffix in new[] { "c", "a", "b" })
        {
            using var written = await client.PutAsJsonAsync(new Uri($"/api/platform/settings/paging.{suffix}", UriKind.Relative), new { value = suffix });
            Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);
            Assert.Empty(await written.Content.ReadAsByteArrayAsync());
        }
        using var response = await client.GetAsync(new Uri("/api/platform/settings/?scope=paging&page=2&limit=2", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("total").GetInt64());
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(2, body.GetProperty("limit").GetInt32());
        Assert.Equal(2, body.GetProperty("totalPage").GetInt64());
        Assert.Equal("paging.c", Assert.Single(body.GetProperty("data").EnumerateArray()).GetProperty("key").GetString());
        using var invalid = await client.GetAsync(new Uri("/api/platform/settings/?scope=paging&page=0&limit=2", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task AcceptedAuditAndDuplicate_AreBothSuccess_WithoutLosingTheirHttpMeaning()
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        var message = new { messageId = Guid.NewGuid(), action = "probe.created", subjectType = "probe", subjectId = "1", detail = "{}" };
        foreach (var expected in new[] { HttpStatusCode.Accepted, HttpStatusCode.OK })
        {
            using var response = await client.PostAsJsonAsync(new Uri("/api/auditing/entries", UriKind.Relative), message);
            Assert.Equal(expected, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("success").GetBoolean());
            Assert.Equal((int)expected, body.GetProperty("code").GetInt32());
            Assert.Equal(expected == HttpStatusCode.Accepted ? "Accepted" : "Duplicate", body.GetProperty("data").GetProperty("outcome").GetString());
        }
    }

    [Fact]
    public async Task GatewayRateLimit_ReturnsCommonProblemAndKeepsRetryAfter()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        gateway.UseRoutes([new RouteDefinition
        {
            RouteId = "limited", ClusterId = "backend", Path = "/limited", RequireAuthentication = false,
            RateLimitPolicy = "gateway.default",
        }]);
        using var client = gateway.CreateClient();
        for (var index = 0; index < 2; index++)
        {
            using var allowed = await client.GetAsync(new Uri("/limited", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal("backend", await allowed.Content.ReadAsStringAsync());
        }
        using var response = await client.GetAsync(new Uri("/limited", UriKind.Relative));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(429, body.GetProperty("code").GetInt32());
        Assert.Equal("http.429", body.GetProperty("errorCode").GetString());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-TraceId")), body.GetProperty("traceId").GetString());
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }

    [Theory]
    [InlineData("GET", "/missing-route", 404)]
    [InlineData("DELETE", "/api/identity/login", 405)]
    [InlineData("GET", "/api/platform/settings/", 400)]
    [InlineData("DELETE", "/api/files/1", 401)]
    public async Task FrameworkFailure_UsesCommonErrorContract(string method, string path, int status)
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(status, body.GetProperty("code").GetInt32());
        Assert.Equal($"http.{status}", body.GetProperty("errorCode").GetString());
        if (status == 401)
        {
            Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
        }
    }

    [Fact]
    public async Task BusinessFailure_PreservesErrorCodeAndProblemDetails_WithCommonFields()
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
            new { userName = "invalid-user", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(400, body.GetProperty("code").GetInt32());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("identity.password.too_short", body.GetProperty("errorCode").GetString());
        Assert.Equal(body.GetProperty("title").GetString(), body.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-TraceId")), body.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task PlatformRead_UsesTheSameEnvelope_AndPreservesNullBusinessValue()
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        using var response = await client.GetAsync(new Uri("/api/platform/settings/site.title", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(200, body.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task CreatedUser_ReturnsSuccessEnvelopeAndPreservesLocation()
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var response = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
            new { userName = "response-user", password = "response-test-password" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(201, body.GetProperty("code").GetInt32());
        Assert.Equal("Success", body.GetProperty("message").GetString());
        var id = body.GetProperty("data").GetProperty("userId").GetInt64();
        Assert.Equal($"/api/identity/users/{id}", response.Headers.Location?.OriginalString);
        Assert.InRange(body.GetProperty("timestamp").GetInt64(), before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-TraceId")), body.GetProperty("traceId").GetString());
    }
}
