using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.Aspire.ServiceDefaults;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.Composition;
using NexusStackNext.Gateway;
using NexusStackNext.Gateway.Routing;
using Yarp.ReverseProxy.Configuration;

// 网关宿主。不变量 8：这个服务由什么组成，一眼看得出来。
//
// 它在边缘做四件事：路由、认证（**验签**——形态已由 ADR-0014 定下：令牌由 Identity 签发、
// 网关只验"是不是我们发的、过期没有"）、关联 ID、限流。
// 它**不**做授权 —— 那是各上下文的事（ADR-0003）。
var builder = WebApplication.CreateBuilder(args);

// 日志与配置中心：两个宿主完全相同的那几行（见 src/Composition）。
builder.AddNexusStackLogging();
builder.AddNexusStackAgileConfig();

// OpenTelemetry 追踪、健康检查、HTTP 韧性与服务发现。
// **它不依赖 Aspire**：有 OTLP 端点就导出，没有就只是不导出（ADR-0005）。
builder.AddNexusStackServiceDefaults();

// ---------- 1. 路由表：载入、校验，无效就拒绝启动 ----------
var configuredPath = builder.Configuration["Gateway:RouteTablePath"] ?? "routes.json";
var routeTablePath = Path.IsPathRooted(configuredPath)
    ? configuredPath
    : Path.Combine(AppContext.BaseDirectory, configuredPath);

// 参照仓库的网关在生产环境因配置缺失而直接起不来，错误信息还不可操作
// （空连接串异常）。这里同样拒绝启动，但把"哪儿错了、错在哪"说清楚。
var routeTableStore = new FileRouteTableStore(routeTablePath);
var routeTable = await routeTableStore.LoadAsync();

if (routeTable.IsFailure)
{
    throw new InvalidOperationException(
        $"路由配置不可用（{routeTablePath}）：{routeTable.Error.Code} — {routeTable.Error.Message}");
}

var (routes, clusters) = YarpConfigMapper.ToYarp(routeTable.Value);

// 路由表引用的限流策略必须都注册过。引用一个不存在的策略名，后果是
// "以为限流开了、其实没开"——那正是参照仓库式的静默失效。这里让它在**启动时**就失败。
var unknownRateLimitPolicies = routeTable.Value.Routes
    .Select(static route => route.RateLimitPolicy)
    .Where(static policy => !string.IsNullOrWhiteSpace(policy))
    .Distinct(StringComparer.Ordinal)
    .Where(policy => !string.Equals(policy, YarpConfigMapper.DefaultRateLimitPolicy, StringComparison.Ordinal))
    .ToList();

if (unknownRateLimitPolicies.Count > 0)
{
    throw new InvalidOperationException(
        $"路由表引用了未注册的限流策略：{string.Join("、", unknownRateLimitPolicies)}。"
        + $"当前已注册：{YarpConfigMapper.DefaultRateLimitPolicy}。");
}

var rateLimitPermitLimit = builder.Configuration.GetValue("Gateway:RateLimit:PermitLimit", 100);
var rateLimitWindowSeconds = builder.Configuration.GetValue("Gateway:RateLimit:WindowSeconds", 60);

// ---------- 2. 显式组装 ----------
builder.Services.AddNexusStackApplication();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// 可达性探测：**两个消费者**共用它——就绪检查与实时推送。
// 一个消费者时它只是一段代码；两个之后，"探测后端是否可达"才成为一道真的缝。
builder.Services.AddHttpClient();
builder.Services.AddSingleton(routeTable.Value);
builder.Services.AddSingleton<ClusterReachabilityProbe>();

// OpenAPI 聚合：把网关自己与每个后端的文档合并成一份对外提供。
builder.Services.AddSingleton<DownstreamOpenApiAggregator>();

builder.Services.AddHealthChecks().AddCheck<ClusterReachabilityHealthCheck>("clusters");

// 边缘的实时通道。hub 必须在网关上，因为客户端只到得了边缘（部署不变量）。
builder.Services.AddSignalR();
builder.Services.AddHostedService<ClusterStatusBroadcaster>();

// 存储也注册进容器：路由管理端点在写入时要走同一个实现（原子写）。
builder.Services.AddSingleton<IRouteTableStore>(routeTableStore);

// ---------- 认证：验签（ADR-0003、ADR-0014）----------
//
// 认证形态已定（ADR-0014）：令牌由 Identity 签发、**网关验签**、授权在各上下文。
// 网关用与签发方同一把 HS256 密钥——所以这里只验"这个令牌是不是我们发的、过期没有"，
// 不判断"他能不能做这件事"（那是上下文的事）。
var gatewayJwtKey = builder.Configuration["Jwt:SigningKey"];

if (string.IsNullOrWhiteSpace(gatewayJwtKey) || System.Text.Encoding.UTF8.GetByteCount(gatewayJwtKey) < 32)
{
    // **降级而不是崩溃，而且要说清楚。**
    //
    // 参照仓库的网关在配置缺失时直接启动失败，错误还是"空连接串"这种不可操作的信息；
    // 而它的受保护路由实际上从来没被保护过。这里的取舍是：
    // 进程起来（编排系统不会重启风暴，运维能看到健康检查与日志），
    // 但**所有受保护路由一律 401**——拒绝，而不是放行。
    Console.Error.WriteLine(
        "[网关] 未配置 Jwt:SigningKey（或在长度上不足 32 字节）：受保护路由将一律返回 401。"
        + "请从配置中心或环境变量提供签名密钥。");

    builder.Services
        .AddAuthentication(GatewayAuthentication.NotConfiguredScheme)
        .AddScheme<AuthenticationSchemeOptions, NotConfiguredAuthenticationHandler>(
            GatewayAuthentication.NotConfiguredScheme,
            _ => { });
}
else
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            // **在 lambda 里面读配置**：注册那一刻读到的值会漏掉之后加上的配置源
            // （这一课在本仓已经踩过三次）。
            var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    System.Text.Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });
}

builder.Services.AddAuthorization(options =>
{
    GatewayAuthorizationPolicies.AddGatewayPolicies(options);

    options.AddPolicy(
        YarpConfigMapper.AuthenticatedPolicy,
        policy => policy.RequireAuthenticatedUser());
});

// **用 `InMemoryConfigProvider` 而不是 `LoadFromMemory`。**
//
// 后者在启动时把配置拷进去，之后改不了——于是路由管理 API 保存成功、流量照旧走老规则。
// 前者可以被 `RouteTableReloader` 更新，管理 API 才是真的。
var proxyConfigProvider = new InMemoryConfigProvider(routes, clusters);
builder.Services.AddSingleton(proxyConfigProvider);
builder.Services.AddSingleton<IProxyConfigProvider>(proxyConfigProvider);
builder.Services.AddSingleton<RouteTableReloader>();
builder.Services.AddReverseProxy();

// 限流。参照仓库的网关是"裸转发"——没有限流、没有关联 ID、没有重试（review/03）。
// 队列长度设为 0：超限直接 429，而不是把请求排起来拖长尾延迟。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(YarpConfigMapper.DefaultRateLimitPolicy, limiter =>
    {
        limiter.PermitLimit = rateLimitPermitLimit;
        limiter.Window = TimeSpan.FromSeconds(rateLimitWindowSeconds);
        limiter.QueueLimit = 0;
    });
});

// 路由级 Timeout 需要 ASP.NET Core 的 Request Timeouts 中间件配套。
// 少了它 YARP 会在**每个请求**上抛错（"timeout was not applied ... ensure
// UseRequestTimeouts() is called"）——响亮地失败，好过一个"配了却不生效"的超时。
builder.Services.AddRequestTimeouts();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseAuthorization();

// 网关自己的文档。**放在一个内部路径上**：对外提供的是聚合文档，见下。
app.MapOpenApi("/openapi/gateway.json");

// 对外的是**聚合**文档：网关自己 + 每个后端。
//
// 为什么聚合在网关上：部署不变量说"业务服务不对外暴露，边缘是唯一入口"。
// 生产环境里能对外提供文档的**只有边缘**——把界面挂在业务服务上会绕过它。
app.MapGet(
    "/openapi/v1.json",
    async (DownstreamOpenApiAggregator aggregator, CancellationToken cancellationToken) =>
        Results.Text((await aggregator.GetAsync(cancellationToken)).Json, "application/json"));

// 来源状态。**"文档少了几个接口"必须是看得见的**——
// 否则它与"接口本来就不存在"分不开，而后者会让人以为服务没实现。
app.MapGet(
    "/gateway/openapi/sources",
    async (DownstreamOpenApiAggregator aggregator, CancellationToken cancellationToken) =>
    {
        var aggregated = await aggregator.GetAsync(cancellationToken);

        return Results.Ok(new
        {
            complete = aggregated.Sources.All(static source => source.Ok),
            sources = aggregated.Sources.Select(static source => new
            {
                source.Name,
                source.Url,
                source.Ok,
                source.PathCount,
                source.Detail,
            }),
        });
    });

// API 参考界面。**生产也开**——它挂在边缘上，与部署不变量一致，
// 而且这正是"要在生产看文档"的落点。
// （业务服务上的那个仍然是仅 Development：那里的界面会绕过边缘。）
//
// 指向的是上面那个**聚合**文档，所以这一个页面里能看到网关自己与每个后端的全部接口。
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "NexusStackNext 边缘聚合文档"));

// 实时通道。客户端连到 ws://<edge>/hubs/gateway；服务端在**后端可达性变化时**推送。
app.MapHub<GatewayHub>("/hubs/gateway");

// **存活与就绪在这里是两件事。** 网关自己活着，不代表它背后的服务可达。
// 此前它只报自己的状态——于是所有后端都挂掉时它仍然报健康，编排系统会继续把流量送进来。
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");

// 路由表自述：把"网关现在按什么规则转发"变成可查询的事实，而不是只能读配置文件。
//
// **注意这里没有 Auditing。** 它是只写上下文：事件从消息总线进入，**不经边缘**。
// 给它开一条边缘路由等于让任何人都能注入审计记录。
// "是一个服务"与"在边缘上可达"是两件事——参照仓库从没把这条写下来过。
//
// **它每次读存储，不读启动时的快照。**
// 第一版用的是构造时捕获的 `routeTable`——于是管理 API 改完路由之后，
// 这个"自述"端点还在报旧表。那正是它要防的那种失效：接口说成功、事实没变，
// 而且**看自述端点也看不出来**（它跟着一起说谎）。
app.MapGet("/gateway/routes", async (IRouteTableStore store, CancellationToken cancellationToken) =>
{
    var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

    if (loaded.IsFailure)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: loaded.Error.Message);
    }

    return Results.Ok(new
    {
        routeTablePath,
        routes = loaded.Value.Routes.Select(static route => new
        {
            route.RouteId,
            route.ClusterId,
            route.Path,
            route.Methods,
            transformCount = route.Transforms.Count,
            route.RequireAuthentication,
        }),
    });
});

// 路由表管理。**只认根管理员**——改路由表是系统级动作。
app.MapGatewayRouteAdmin();

app.MapReverseProxy();

app.Run();
