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
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Composition;
using NexusStackNext.Files.Endpoints;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.Identity.Endpoints;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.Platform.Endpoints;
using NexusStackNext.Platform.Infrastructure;
using NexusStackNext.PlatformHost;
using NexusStackNext.Scheduling.Endpoints;
using NexusStackNext.Scheduling.Infrastructure;

if (args is ["operation-journal", ..])
{
    Environment.ExitCode = await OperationJournalCommand.RunAsync(args[1..]);
    return;
}

if (args is ["migrate-operation-journal"])
{
    Environment.ExitCode = await OperationJournalModule.MigrateOperationJournalAsync();
    return;
}

if (args is ["migrate-identity"])
{
    Environment.ExitCode = await IdentityDatabaseCommand.RunAsync();
    return;
}

if (args is ["migrate-platform"])
{
    Environment.ExitCode = await PlatformDatabaseCommand.RunAsync();
    return;
}

if (args is ["migrate-files"])
{
    Environment.ExitCode = await FilesDatabaseCommand.RunAsync();
    return;
}

if (args is ["migrate-auditing"])
{
    Environment.ExitCode = await AuditingDatabaseCommand.RunAsync();
    return;
}

if (args is ["migrate-scheduling"])
{
    Environment.ExitCode = await SchedulingDatabaseCommand.RunAsync();
    return;
}

// 平台能力的**唯一宿主**。不变量 8：这个进程由什么组成，一眼看得出来——
// 下面五行就是它的全部内容，没有 InitApplication(moduleKey)，也没有"我是哪个服务"的运行时枚举。
//
// 为什么是一个进程而不是五个：这五个是**通用子域**——服务于业务，但本身不是业务差异化所在，
// 而且一起演进、一起部署（ADR-0013）。业务上下文仍然各自独立成服务、独立库。
//
// 一个顺带消失的配置风险：五个进程时，每个都要一个不同的 Snowflake WorkerId，
// 配重了就会产生**重复 ID**（不报错，只在数据里留下两条同 ID 的记录）。
// 现在只有一个进程，只需要一个 WorkerId。
var builder = WebApplication.CreateBuilder(args);

// 日志与配置中心（见 src/Composition）。
builder.AddNexusStackLogging();
builder.AddNexusStackAgileConfig();

// OpenTelemetry 追踪、健康检查、HTTP 韧性与服务发现。
// **它不依赖 Aspire**：有 OTLP 端点就导出，没有就只是不导出（ADR-0005）。
builder.AddNexusStackServiceDefaults();

builder.Services.AddNexusStackApplication();

// 依赖 AddNexusStackApplication 注册的 IClock；顺序反了会立刻失败，而不是在运行时。
builder.Services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 101 });

// ---------- 事件总线（配了才接）----------
//
// **没配 RabbitMQ 就什么都不接。** 接一个连不上的总线，结果是一个"每次发布都失败"的总线——
// 它比不接更糟：发件箱投递器会把每条消息记成一次失败尝试，八轮之后全部进死信，
// 而那看起来像"消息有问题"，不像"总线没配"。
//
// 没接总线时的表现是**没有投递器**（`AddNexusStackOutboxDelivery` 不被调用），
// 事件留在发件箱里不再前进——那是一件看得见的事。
var rabbit = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMqOptions>();

if (rabbit is not null && !string.IsNullOrWhiteSpace(rabbit.HostName))
{
    builder.Services.AddNexusStackRabbitMqEventBus(rabbit, PlatformInfrastructureServiceCollectionExtensions.OutboxKey,
        builder.Configuration.GetSection("Platform:Delivery").Get<OutboxDeliveryOptions>());
    builder.Services.AddNexusStackOutboxDelivery(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey,
        builder.Configuration.GetSection("Scheduling:Delivery").Get<OutboxDeliveryOptions>());
    builder.Services.AddNexusStackOutboxDelivery(OperationJournalServiceCollectionExtensions.OutboxKey,
        builder.Configuration.GetSection("OperationJournal:Delivery").Get<OutboxDeliveryOptions>());
    builder.Services.AddNexusStackOutboxDelivery(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey,
        builder.Configuration.GetSection("Identity:Delivery").Get<OutboxDeliveryOptions>());
    builder.Services.AddNexusStackOutboxDelivery(FilesPersistenceServiceCollectionExtensions.OutboxKey,
        builder.Configuration.GetSection("Files:Delivery").Get<OutboxDeliveryOptions>());
}

// 五个平台能力。每一行的顺序就是依赖的顺序，没有隐藏的自动发现。
builder.Services.AddIdentityModule(builder.Configuration, builder.Environment);
builder.Services.AddPlatformModule(builder.Configuration, builder.Environment);
builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment);
builder.Services.AddAuditingModule(builder.Configuration, builder.Environment);
builder.Services.AddFilesModule(builder.Configuration, builder.Environment);
builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "platform");

// ---------- 认证（ADR-0003：网关验签、上下文授权）----------
//
// 网关先验一次并拒绝未认证的请求；上下文**再验一次**，因为"只有网关能到达我"
// 是部署的约定，而不是代码的事实。多验一次的代价是一次本地计算——
// 而少验一次的代价是：某天某个端口被发布出去，谁能直连谁就绕过了整套保护，
// 而所有测试仍然全绿（部署不变量那条）。
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // **配置必须在 lambda *里面* 读，不能在注册那一刻读。**
        //
        // 这是本会话第三次踩同一个坑（前两次是身份模块的 JWT 签发、以及 `JwtOptions` 的绑定）：
        // 注册时读一次值，之后再加的配置源就全都读不到——
        // 测试的 `ConfigureAppConfiguration` 与宿主的 AgileConfig 都是这样。
        // 而这次的症状最难看：空密钥让 `SymmetricSecurityKey` 构造失败，
        // 于是**每个请求**（连公开的 OpenAPI 端点）都 500，失败点离原因很远。
        var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        // 默认声明映射会把 `sub` 改名，于是 `ClaimTypes.NameIdentifier` 与 `sub` 只能取到一个。
        // 关掉它，让声明原样保留——`ClaimsCurrentUser` 两个都试是兜底，不是依赖。
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
            // 默认有 5 分钟时钟偏移容差；令牌本身只有 15 分钟，那等于多给三分之一。
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization();

// `ICurrentUser` 的实现换成**从已认证声明读**的那个。
// 默认实现（`AnonymousCurrentUser`）永远返回 null——它是"认证还没接入"时的正确表现，
// 而现在认证接入了，它就不再是事实。
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();

// 请求授权过滤器（票据 11）。
builder.Services.AddNexusStackAuthorization();

builder.Services.AddHealthChecks();
builder.Services.AddApiResponseContract();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseRouting();
app.UseCorrelationId();
app.UseOperationJournal();
app.UseExceptionHandler();
app.UseApiResponseContract();

// 顺序有意义：先认证（你是谁），再授权（你能不能）。反过来的话，
// 授权过滤器看到的是一个还没有身份的请求——于是每个请求都 401。
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();

// API 参考界面**只在开发环境开**。
//
// 部署不变量说"业务服务不对外暴露，边缘是唯一入口"——而这个界面会暴露**全部** API 面。
// 开发时它是便利；生产里它是一个绕过边缘的信息出口，而所有测试都不会发现这件事。
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "平台宿主"));
}

// 日志慢故障不得消耗业务探测预算；日志依赖的异常通过独立诊断端点保留。
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = static check => !check.Tags.Contains(AuditingDiagnostics.HealthTag),
});
app.MapHealthChecks("/health/logging", new HealthCheckOptions
{
    Predicate = static check => check.Tags.Contains(AuditingDiagnostics.HealthTag),
});

// 五个模块各自的 HTTP 面。路由前缀已经带上下文名（/api/identity、/api/files……），
// 所以它们并到一个进程里**不需要改任何路由**——网关的路由表也只改目标地址。
//
// **授权由模块自己声明**，宿主不必记得替每个模块挂一遍。两种机制，按需要选：
//   · Identity / Platform / Auditing 挂 `NexusStackAuthorizationFilter`——按端点声明的权限键（路由模板:方法）
//     一次读取 Identity 当前已提交会话与许可，不使用诊断权限缓存；
//   · Files 通过同一过滤器检查有效会话，再判断归属；Scheduling 声明自己的端点授权要求。
// 两者都是**进程内的**判定：直连后端也绕不过去。（"边缘是唯一入口"是编排的事实，
// 不是代码的事实——见 AGENTS.md 的部署不变量。）
app.MapIdentityEndpoints();
app.MapPlatformEndpoints();
app.MapSchedulingEndpoints();
app.MapAuditingEndpoints();
app.MapFilesEndpoints();

app.Run();
