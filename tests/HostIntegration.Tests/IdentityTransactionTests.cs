using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Infrastructure;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityTransactionTests
{
    [Fact]
    public async Task FailedRefreshIssuance_WithInMemoryStorage_DoesNotConsumeTheToken()
    {
        await using var app = new SigningApp();
        using var client = app.CreateDefaultClient();
        var credentials = new { userName = "memory-transaction", password = "a-strong-password" };
        using var created = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), credentials);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), credentials);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var request = new { refreshToken = body.GetProperty("refreshToken").GetString() };

        app.FailIssuance = true;
        using var failed = await client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        app.FailIssuance = false;
        using var retried = await client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }

    private sealed class SigningApp : PlatformApp
    {
        public bool FailIssuance { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddTransient<IAccessTokenIssuer>(_ =>
                new JwtAccessTokenIssuer(Options.Create(new JwtOptions
                {
                    SigningKey = FailIssuance ? "too-short" : "integration-test-signing-key-long-enough-for-hs256",
                }))));
        }
    }
}
