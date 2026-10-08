using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.BuildingBlocks.Application.Security;

namespace NexusStackNext.TestSupport;

// External HTTP fixture for tests of business behavior. Revocation journeys use real Identity instead.
internal sealed class SessionAuthorityStub(WebApplication app) : IAsyncDisposable
{
    public string Address => app.Urls.Single();

    public static async Task<SessionAuthorityStub> StartAsync(string signingKey, Func<HttpContext, Task<IResult>>? response = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true,
                ValidIssuer = "nexusstack",
                ValidateAudience = true,
                ValidAudience = "nexusstack",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            };
        });
        builder.Services.AddAuthorization();
        var host = builder.Build();
        host.UseAuthentication();
        host.UseAuthorization();
        host.MapGet("/api/identity/session/v1", response ?? CurrentAsync).RequireAuthorization();
        try { await host.StartAsync(); return new(host); }
        catch { await host.DisposeAsync(); throw; }
    }

    private static Task<IResult> CurrentAsync(HttpContext context)
    {
        var subject = context.User.FindFirst("sub")?.Value;
        var version = context.User.FindFirst(NexusStackClaims.Session)?.Value;
        if (subject is not ("42" or "43" or "admin" or "test-operator" or "test-reader") || version != "0") { return Task.FromResult<IResult>(Results.Unauthorized()); }
        return Task.FromResult<IResult>(Results.Json(new
        {
            success = true,
            code = 200,
            data = new { contractVersion = 1, subject, sessionVersion = 0L.ToString(CultureInfo.InvariantCulture), isRoot = subject is not ("43" or "test-reader") },
        }));
    }

    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
