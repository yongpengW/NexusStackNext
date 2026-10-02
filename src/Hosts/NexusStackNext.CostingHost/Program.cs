using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.Aspire.ServiceDefaults;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Composition;
using NexusStackNext.Costing.Endpoints;
using NexusStackNext.Costing.Infrastructure;

if (args is ["migrate-costing"])
{
    try
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Costing");
        if (string.IsNullOrWhiteSpace(connection)) { throw new InvalidOperationException(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await CostingDatabase.MigrateAsync(connection, timeout.Token);
        Console.WriteLine("Costing migrations applied.");
        return 0;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Costing migration failed; check ConnectionStrings__Costing, database availability and migration privileges.");
        return 1;
    }
}

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddNexusStackLogging();
    builder.AddNexusStackAgileConfig();
    builder.AddNexusStackServiceDefaults();
    builder.Services.AddNexusStackApplication();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
    builder.Services.AddCostingModule(builder.Configuration);
    builder.Services.AddApiResponseContract();
    builder.Services.AddOpenApi();
    var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
    if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32 || string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
    {
        throw new InvalidOperationException("必须配置 Jwt:SigningKey（至少 32 字节）、Issuer 与 Audience。");
    }
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };
    });
    // 样板只向已认证的根操作者开放；后续业务角色必须由本上下文定义。
    builder.Services.AddAuthorizationBuilder().AddPolicy("costing-operator", policy =>
        policy.RequireAuthenticatedUser().RequireClaim(NexusStackClaims.Root, "true"));
    var app = builder.Build();
    app.UseExceptionHandler();
    app.UseApiResponseContract();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapCostingEndpoints();
    app.MapOpenApi();
    app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready");
    await app.RunAsync();
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Costing startup failed; check ConnectionStrings:Costing, migrate-costing, Jwt and Costing:Tasks configuration.");
    return 1;
}
