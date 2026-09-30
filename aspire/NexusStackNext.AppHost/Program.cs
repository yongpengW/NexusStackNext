// ============================================================================
// 本地编排：**只编排应用进程，中间件走外部依赖**（ADR-0005）。
//
// 这里**没有** AddPostgres()/AddRedis()/AddRabbitMQ() —— 那些会尝试拉容器，
// 而这台机器上没有容器运行时，更重要的是：中间件本来就在外面跑着
// （一套现成的 PostgreSQL / Redis / RabbitMQ / AgileConfig / Seq，地址在 env/*.dev 里）。
//
// 所以 AppHost 做的是两件事：把两个进程拉起来，并把**连接信息**注入它们。
// 这正是"编排"这件事的最小含义，也是它唯一不可替代的部分。
//
// 本文件**不是**模板的一部分，而这也是一处**可验证**的设计：
// 把整个 aspire/ 目录删掉，两个宿主仍然能用环境变量独立启动——
// 因为它们读的是配置，不是 Aspire 的注入（ADR-0005 后果）。
// ============================================================================

var builder = DistributedApplication.CreateBuilder(args);

// ---------- 外部依赖：从环境读，读不到就说清楚缺什么 ----------
//
// **可操作的错误，不是空连接串异常。**
// 参照仓库的网关死在这一点：生产配置缺失时它抛的是"空连接串"，
// 而那句话不告诉你缺哪个变量、也不知道去哪儿找。
var missing = new List<string>();

string Require(string name)
{
    var value = Environment.GetEnvironmentVariable(name);

    if (string.IsNullOrWhiteSpace(value))
    {
        missing.Add(name);
        return string.Empty;
    }

    return value;
}

var postgres = Require("NEXUSSTACK_DB");
var redis = Require("NEXUSSTACK_REDIS");
var rabbit = Require("NEXUSSTACK_RABBITMQ");
var otlp = Environment.GetEnvironmentVariable("NEXUSSTACK_OTLP") ?? string.Empty;

if (missing.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("缺少下面这些环境变量，AppHost 无法给出可用的连接信息：");
    Console.Error.WriteLine();

    foreach (var name in missing)
    {
        Console.Error.WriteLine($"  {name}");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("它们的值在 env/ 目录下的 *.dev 文件里（那些文件不进仓库）。");
    Console.Error.WriteLine("本地起法：先 `./scripts/run-apphost.ps1`，它会替你读进来。");
    Console.Error.WriteLine("只想跑单个服务的话，不需要 AppHost —— 见 README 的\"两个进程\"一节。");
    Console.Error.WriteLine();

    return 1;
}

// ---------- 两个进程 ----------
//
// **只有两个。** 五个平台能力（Identity / Platform / Scheduling / Auditing / Files）
// 由 ADR-0013 合成一个宿主，而不是五个服务——所以本票原文说的
// "编排 5 个服务 + 网关"已经过时。
var platform = builder
    .AddProject<Projects.NexusStackNext_PlatformHost>("platform")
    .WithEnvironment("ConnectionStrings__PostgreSQL", postgres)
    .WithEnvironment("RabbitMQ__HostName", Host(rabbit))
    .WithEnvironment("Redis__Configuration", redis)
    // OTLP 端点：**配了就导出，没配就只是不导出**（ServiceDefaults 的取舍，ADR-0005）。
    //
    // 这里**不注入 Seq**：本仓的 Serilog 只有 Console sink，而日志模板配了
    // `Enrich: WithSpan`——于是每条日志自带 TraceId，跨服务关联在控制台就看得到。
    // 第一版往这里注了一个 `Serilog__WriteTo__0__Args__serverUrl`，那是我照参照仓库
    // 想当然写的（参照仓库用 Seq，本仓不用），注进去没有任何东西消费它。
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
    // 固定端口：网关的路由表指向它，端口漂了路由就断了。
    .WithEndpoint(5191, 5191, "http", isProxied: false);

builder
    .AddProject<Projects.NexusStackNext_Gateway>("gateway")
    .WithEnvironment("Gateway__RouteTablePath", "routes.json")
    .WithEndpoint(5190, 5190, "http", isProxied: false)
    .WaitFor(platform);

// 从连接串里取主机名——这里只用来示意，真正解析连接串的是各宿主自己。
static string Host(string value)
{
    foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var pair = part.Split('=', 2);
        if (pair.Length == 2 && pair[0].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            return pair[1].Trim();
        }
    }

    return value;
}

await builder.Build().RunAsync();
return 0;
