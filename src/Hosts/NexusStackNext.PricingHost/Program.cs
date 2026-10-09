using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.Aspire.ServiceDefaults;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Composition;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;

if (args is ["operation-journal", ..])
{
    return await OperationJournalCommand.RunAsync(args[1..]);
}

if (args is ["migrate-operation-journal"])
{
    return await OperationJournalModule.MigrateOperationJournalAsync();
}

if (args is ["migrate-pricing"])
{
    try
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Pricing");
        if (string.IsNullOrWhiteSpace(connection)) { throw new InvalidOperationException(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await PricingDatabase.MigrateAsync(connection, timeout.Token);
        Console.WriteLine("Pricing migrations applied.");
        return 0;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Pricing migration failed; check ConnectionStrings__Pricing, database availability and migration privileges.");
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
    builder.Services.AddIdentitySessionAuthority(builder.Configuration);
    builder.Services.AddPricingModule(builder.Configuration);
    var exportsEnabled = builder.Configuration.GetValue<bool>("Pricing:Exports:Enabled");
    if (exportsEnabled) { builder.Services.AddPricingExports(builder.Configuration, builder.Environment.EnvironmentName); }
    builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "pricing");
    var rabbit = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMqOptions>();
    if (rabbit is not null && !string.IsNullOrWhiteSpace(rabbit.HostName))
    {
        builder.Services.AddNexusStackRabbitMqEventBus(rabbit, OperationJournalServiceCollectionExtensions.OutboxKey,
            builder.Configuration.GetSection("OperationJournal:Delivery").Get<OutboxDeliveryOptions>());
    }
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
    // 会话与操作许可由 Identity 裁决，端点声明操作键，业务对象规则仍由本上下文拥有。
    builder.Services.AddAuthorizationBuilder().AddPolicy("pricing-operator", policy =>
        policy.RequireAuthenticatedUser().AddRequirements(new CurrentOperationRequirement()));
    var app = builder.Build();
    app.UseRouting();
    app.UseCorrelationId();
    app.UseOperationJournal();
    app.UseExceptionHandler();
    app.UseApiResponseContract();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapPricingEndpoints();
    if (exportsEnabled) { app.MapPricingExportEndpoints(); }
    app.MapOpenApi();
    app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = static check => !check.Tags.Contains(AuditingDiagnostics.HealthTag),
    });
    app.MapHealthChecks("/health/logging", new HealthCheckOptions
    {
        Predicate = static check => check.Tags.Contains(AuditingDiagnostics.HealthTag),
    });
    await app.RunAsync();
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Pricing startup failed; check ConnectionStrings:Pricing, migrate-pricing, ConnectionStrings:OperationJournal, migrate-operation-journal, Jwt, IdentitySession, Pricing:Tasks and Pricing:Exports configuration.");
    return 1;
}
