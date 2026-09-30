# 03 宿主与集成评审

评审范围（只读，未修改仓库）：

- `Host/NexusStack.WebAPI/**` — 17 个 Controller + `Program.cs` + `Migrations/**` + 5 份 `appsettings*.json` + `agile/config/*.cache` + `Dockerfile` + `.http`
- `Host/NexusStack.Gateway/**` — 2 个 Controller + `Program.cs` + `appsettings.json` / `appsettings.Development.json` + `proxy-config.json` + `Dockerfile`
- `Domain/NexusStack.Swagger/**`、`Domain/NexusStack.Serilog/**`、`Domain/NexusStack.Excel/**`
- 为判断"宿主如何被组合、宿主之间有什么隐藏耦合"，只读取证了 `Domain/NexusStack.Core/ServiceCollectionExtensions.cs`（宿主的真实启动代码）、`Domain/NexusStack.Core/Gateway/**`、`Domain/NexusStack.Core/Filters/**` 与 `Infrastructure` 的少量文件；这些库的完整评审以 `01-core-and-infrastructure.md` 为准。

---

## 概览

### WebAPI 宿主里到底有什么

`Host/NexusStack.WebAPI/Program.cs` 只有 5 行有效代码：定义 `moduleKey = "nexusstack_web_api"`，然后 `await builder.InitAppliation(moduleKey, moduleTitle)`（`Program.cs:3-8`）。**所有** 组合逻辑都在 `Domain\NexusStack.Core\ServiceCollectionExtensions.cs` 里，也就是说 WebAPI 这个"最大的 host"本身几乎不含代码，它只是 `NexusStack.Core` 的一个启动壳。

| WebAPI 组成 | 内容 | 证据 |
|---|---|---|
| 入口 | 5 行，转发到 `InitAppliation` | `Program.cs:3-8` |
| 控制器 | 17 个（含 2 个空/纯基类）：`User`(368) `OpenAppConfig`(313) `Role`(235) `Menu`(219) `Region`(211) `Token`(152) `OperationLog`(125) `GlobalSetting`(79) `AsyncTask`(75) `Download`(63) `SeedDataTask`(59) `ScheduleTask`(58) `TokenLog`(48) `Health`(22) `Redis`(22) `Base`(12) `Permission`(11) | `Controllers/*.cs` |
| 其中空壳 | `PermissionController` 只有一个空类体；`BaseController` 只重复了基类的 `[Route]` | `Controllers\PermissionController.cs:8-11`、`Controllers\BaseController.cs:9-13` |
| 中间件 | 无自定义中间件；全部走共享管线的 `UseApp` | `Core\ServiceCollectionExtensions.cs:278-327` |
| 过滤器 | 无宿主自有过滤器；4 个全局过滤器全部注册在共享库里 | `Core\ServiceCollectionExtensions.cs:225-238` |
| 迁移 | 唯一一份 EF 迁移 + Snapshot（`MigrationsAssembly` 硬编码指向本宿主） | `Migrations\20260307033208_InitialDatabase.cs`、`Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:40` |
| 配置 | 5 份环境 appsettings + 2 份 AgileConfig 落地缓存（含明文密钥） | `appsettings*.json`、`agile\config\*.cache` |
| 静态资源 | 空 `wwwroot/index.html` + `/static` 映射到上传目录 | `wwwroot\index.html:1-10`、`Core\ServiceCollectionExtensions.cs:439-444` |

控制器服务的对象**没有任何业务领域**：全部是"用户/角色/菜单/权限/区域/全局设置/定时任务/异步任务/登录日志/开放应用配置"。唯一带一点业务气味的是 `OpenAppConfigController`（开放应用的密钥、通知、Webhook、事件回调），但它操作的是 `AppConfig`/`AppWebhookConfig` 这类平台配置实体，仍然属于"脚手架自带的后台管理"，不是某个限界上下文。模板宣称的能力（YARP、Swagger、Serilog+Seq、SignalR、AgileConfig、Docker）里，**SignalR 的 Hub（`/hubs/notification`）根本不在 WebAPI 进程里映射**，只在 `CoreServiceType.MQService` 分支映射（`Core\ServiceCollectionExtensions.cs:313-317`）。

### Gateway 做了什么

`Host/NexusStack.Gateway/Program.cs:4-9` 同样只有一行组合调用，区别是传入 `CoreServiceType.Gateway`。网关实际内容：

- **YARP 动态路由**：自定义 `IProxyConfigProvider`（`DynamicProxyConfigProvider`）从 JSON 存储读路由，注册方式为 `AddReverseProxy().LoadFromMemory([], []).Services.AddSingleton<IProxyConfigProvider>(...)`（`Core\ServiceCollectionExtensions.cs:219-222`），`app.MapReverseProxy()` 无参数（`Core\ServiceCollectionExtensions.cs:321`）。
- **路由/集群管理 API**：`ProxyManagementController` 提供 routes/clusters/config 的 GET/POST/PUT/DELETE + `POST reload` 共 11 个动作（`Controllers\ProxyManagementController.cs:29-241`），对外前缀由 `Controllers\BaseController.cs:9` 的 `[Route("api/gateway/[controller]")]` 决定。
- **除此之外什么都没有**：没有认证方案、没有限流、没有关联 ID、没有重试/熔断/超时策略、没有服务发现、没有对后端的身份转换（`Core\ServiceCollectionExtensions.cs:199-223`）。

### 三个 helper 库做什么

| 库 | 实质 | 行数 | 结论 |
|---|---|---|---|
| `NexusStack.Swagger` | `AddSwaggerGen(name,title)` + `UseSwagger(app,...)`，把 CSS/JS 内联进 `HeadContent`，另加一个"接口授权登录"弹窗脚本 | 104 + 201(js) + 24(css) | 薄封装 + 一段与模板其余部分脱节的浏览器脚本；有真实缺陷（见发现 12） |
| `NexusStack.Serilog` | `UseLog()` 读配置 + 3 个 enricher（IP / WorkerId / TokenId），`WithToken` 从**配置期创建的泄漏 scope**里解析 scoped 服务 | 33+44+26+29+23 | 真正的日志管道不在代码里，而在 AgileConfig（见发现 13） |
| `NexusStack.Excel` | 三套 Excel/PDF 引擎（EPPlus 8.7.1 / NPOI 2.8.1 / iTextSharp-LGPL），导出（模板渲染、DataTable、字典、Stream）、导入（`ExcelReader`）、PDF 发票 | 1258 | 唯一被宿主真正调用的只有 `ExportExcelHelper.ExportToExcel` 和 `ExcelEncryptHelper.EncryptExcel` 两个静态方法；其余约 800 行无人使用（见发现 17） |

**一句话结论**：宿主层不是"两个 host + 三个集成库"，而是**一个共享的、反射驱动的单体启动器被两个 Program.cs 分别调用**；网关因此继承了 WebAPI 的全部依赖（EF Core/PostgreSQL、Redis、RabbitMQ、OSS、Mapster、审计过滤器），同时又没有获得任何边缘能力（认证、限流、韧性、追踪）。动态路由的**抽象设计**是这份代码里质量最高的部分之一，但它的**运行形态**（本机 JSON 文件、每副本一份、管理 API 打不开）在微服务下不可用。此外，模板的打包配置会把生产密钥随模板一起分发（发现 1，最高优先级）。

---

## 发现

### 1. 【最高优先】模板会带着生产密钥一起打包分发（阿里云 AK/SK、数据库/Redis/RabbitMQ 口令）

**证据**

`Host/NexusStack.WebAPI/agile/config/` 下有两个 AgileConfig 客户端落地缓存文件，内容是完整配置的 JSON 数组，其中明文包含：

- `agile\config\nexusdtack_api.agileconfig.client.configs.cache:1` — `AliOSS:ConfigurationCenter.AccessKeySecret = "<已脱敏>"`、`AliOSS:ConfigurationCenter.AccessKeyId = "<AccessKeyId>"`、`AliOSS:DownloadCenter.AccessKeyId = "<AccessKeyId>"` / `AccessKeySecret = "<已脱敏>"`、`AliSMS.ApiKey = "<AccessKeyId>"` / `ApiSecret = "orD8t7qgk4UQYad4spFkGAsJYtC7g7"`、`ConnectionStrings.PostgreSQL = "Host=<服务器IP>;Port=5432;Database=nexusstack;Username=<已脱敏>;Password=<已脱敏>;..."`、`Redis.ConnectionString = "<服务器IP>:6379,user=leowang,password=<已脱敏>,defaultDatabase=0"`、`RabbitMQ.Password = "<已脱敏>"`、`RabbitMQ.Username = "nexusstack_client"`
- `agile\config\nexusstack_api.agileconfig.client.configs.cache:1` — 另一套：`Host=<服务器IP>`、`Username=<已脱敏>;Password=<已脱敏>`、`Redis ... password=<已脱敏> = leowang`

这两个文件虽然**未被 git 跟踪**（`git ls-files Host/NexusStack.WebAPI/agile` 无输出），但模板打包是按目录内容打的，且打包配置**显式包含一切**：

- `NexusStack.Template.csproj:18` `<NoDefaultExcludes>true</NoDefaultExcludes>`；`:22` `<Content Include="**\*" Exclude="**\bin\**;**\obj\**;**\.vs\**;**\.git\**;**\.idea\**;**\packages\**;**\*.user;**\*.suo" />`
- `.template.config\template.json:124-134` 的排除清单同样只有 `bin/obj/.vs/.git/.idea/packages/*.user/*.suo/.template.config`
- 没有任何一处排除 `agile/**`、`*.cache`、`.env`

也就是说 `dotnet pack NexusStack.Template.csproj` → `dotnet new nexusstack` 会把这两个文件原样写进**每一个新建项目**，而该包按 `NexusStack.Template.csproj:10-12` 的 `PackageProjectUrl = github.com/yongpengW/NexusStack` 是要发到公共源的。`appsettings.{Development,Production,Staging,Test}.json:4` 里的 AgileConfig `secret`（`<已脱敏>` / `yF2mM4sS8gZ0uA0v`）虽然被 `.dockerignore:22` 挡在镜像之外，却会随模板发出。

**影响**

生产环境凭证多路泄漏：阿里云 OSS/SMS 主账号 AK（可读写 Bucket `cwchina-erppos-resource`、`cwchina-erppos-configuration`）、可直连的 PostgreSQL/Redis/RabbitMQ 口令与内网 IP（`<服务器IP>-205`、`<服务器IP>`）。任何拿到模板包或通过模板生成项目的人都能拿到一份真实生产凭证。这是整个评审范围内最严重的问题，且**修复成本极低**（删文件 + 加排除 + 轮换密钥），应立即执行。

**建议**

1. 立刻删除这两个缓存文件，并把 `**/agile/**`、`**/*.cache`、`appsettings.*.json` 的敏感键加入 `.template.config/template.json` 的 `exclude` 与 `NexusStack.Template.csproj` 的 `Content Exclude`（`NoDefaultExcludes=true` 时必须白名单式列举，不能靠默认排除）。
2. 轮换上述全部凭证（AK/SK 一旦进入过任何包/仓库即视为已泄漏）。
3. 模板只保留 `appsettings.json` + 占位符（`__AGILECONFIG_SECRET__`），其余环境文件改为首次运行生成或由部署平台注入。
4. 在 `.gitignore` 中加入 `**/agile/**`，并在 CI 增加一条"仓库内不得出现 `AccessKeySecret|Password=` 明文"的密钥扫描门禁。

### 2. 【最高优先】网关的动态路由管理 API 现在打不开：全局鉴权过滤器 + 宿主里不存在的认证方案

**证据**

- 全局过滤器对**所有**控制器生效，包括网关的：`Domain\NexusStack.Core\ServiceCollectionExtensions.cs:234` `options.Filters.Add<RequestAuthorizeFilter>();`
- 该过滤器对未认证请求直接短路，且没有任何 `[AllowAnonymous]` 例外：`Core\Filters\RequestAuthorizeFilter.cs:54-60`
  ```csharp
  if (context.HttpContext.User.Identity?.IsAuthenticated != true) { ... context.Result = new RequestJsonResult(new RequestResultModel(401, PleaseLogin, null)); return Task.CompletedTask; }
  ```
- `ProxyManagementController` 没有任何 `[AllowAnonymous]`：`Gateway\Controllers\ProxyManagementController.cs:8`（继承 `BaseController` → `ApiControllerBase`）
- 而 `ApiControllerBase` 把认证方案**编译期硬编码**成只有 WebService 宿主才注册的那个：`Core\ApiControllerBase.cs:16` `[ApiController, Authorize(AuthenticationSchemes = "Authorization-Token"), Route("api/[controller]")]`
- 该方案只在 WebService 分支注册：`Core\ServiceCollectionExtensions.cs:179-182`；网关分支只调用了 `AddAuthorization()`，**没有** `AddAuthentication(...)`：`Core\ServiceCollectionExtensions.cs:209`
- 网关也没有任何其他入口能产生已认证身份（全仓 `AllowAnonymous` 只出现在 WebAPI 的 `TokenController`/`HealthController`）

于是网关的 11 个管理端点必然落到未认证分支：全局过滤器（Scope=Global）先于控制器上的 `[Authorize]`（Scope=Controller）执行并短路，返回 `code:401`（HTTP 仍是 200，见发现 8）。若过滤器顺序被改变，则改为抛出 `No authentication handler is registered for the scheme 'Authorization-Token'`。**两条路都不通。**

**影响**

模板宣称的"live route management API（GET/POST/PUT/DELETE /routes）"实际不可用：无法在不重启网关的前提下增删路由，动态路由的核心卖点失效；同时它暴露了更本质的设计问题——网关宿主没有自己的认证方案，说明"谁有权改路由"这个边缘安全边界从未被设计过。

**建议**

1. 网关独立注册一个**管理面**认证方案（管理 Token/JWT，或仅内网 mTLS + IP 白名单），把管理端点挂到独立的 `MapGroup("/admin")` 上并加 `RequireAuthorization("GatewayAdmin")`，不要复用业务 Token 方案。
2. `ApiControllerBase` 不应写死 `Authorization-Token`；把方案名放到常量/选项里，或让每个宿主通过 `AddAuthentication` 注册名为 `"Bearer"` 的默认方案。
3. 管理端点默认只在 `Development` 暴露；生产要求显式开关 + 审计日志（现在 `routes` 的写操作只走 `OperationLogActionFilter`，且该过滤器依赖 RabbitMQ）。
4. 给 `ProxyManagementController` 补集成测试（`WebApplicationFactory`）断言"未认证 → 401"，否则这类缺陷无法在模板里被发现。

### 3. 【最高优先】网关在 Production/Staging/Test 下起不来：缺环境配置文件 + 缺 Redis 配置会直接抛异常

**证据**

- 网关只有两份配置文件：`Host\NexusStack.Gateway\appsettings.json:1-19`（只有 `Logging` + `LogSetting`）与 `appsettings.Development.json:1-17`。**没有** `appsettings.Production.json` / `Staging` / `Test`（WebAPI 有 5 份）。
- `appsettings.Development.json:5` 的 AgileConfig `"nodes": ""` —— 外部配置中心地址是空的，网关目录下也没有 `agile/config` 缓存目录（WebAPI 有）。
- 共享管线对**每个**宿主都执行 `app.UseRedis(app.Configuration)`：`Core\ServiceCollectionExtensions.cs:295`
- 而 Redis 初始化在连接串为空时**抛异常**：`Domain\NexusStack.Redis\ServiceCollectionExtensions.cs:22` + `:38-41`
  ```csharp
  options.ConnectionString = configuration.GetSection("Redis:ConnectionString").Value!;
  ...
  if (string.IsNullOrWhiteSpace(redisConnectionString)) throw new Exception("Redis连接字符串不能为空");
  ```

网关的配置来源只剩：环境变量 / user-secrets（`Gateway\NexusStack.Gateway.csproj:7` 有 `UserSecretsId`）/ 空的 AgileConfig。因此在容器或 CI 里（生产最可能的方式）**启动即异常退出**。同样的静默依赖还有：`AddEFCoreAndPostgreSQL`（`Core\...:144`）需要 `ConnectionStrings:PostgreSQL`，`AddRabbitMQ`（`:252`）需要 `RabbitMQ:*`，`AddAliyunOSS`（`:155`）需要 `AliOSS:*`，`AddHttpLogging`（`:124-130`）、`AddAllMapster`（`:250`）也都无条件执行。

**影响**

"网关可独立部署"这一微服务前提不成立：网关无法在没有业务库/Redis/MQ/OSS 的环境里启动，也就无法作为纯边缘组件横向扩容。同时暴露出组合方式的结构性问题——共享的 `AddBuilderServices` 让每个宿主都被迫依赖全栈基础设施（详见发现 6）。

**建议**

1. 把 `AddBuilderServices` 按宿主能力拆成显式模块：`AddHosting()` / `AddApiSurface()` / `AddPersistence()` / `AddCache()` / `AddMessaging()` / `AddEdge()`；网关只调 `AddEdge()` + 它真正需要的（`ProxyConfigLockService`、`IProxyConfigStore`）。
2. 配置缺失必须**启动期快速失败并给出可操作信息**（用 `ValidateOnStart` + `IValidateOptions<T>` 替代 `UseRedis` 里的运行时 `throw new Exception`，并统一错误文案与退出码）。
3. 网关补齐 `appsettings.{Production,Staging,Test}.json`（至少包含 AgileConfig 节点与 `Cors`），或改为从环境变量读取 AgileConfig 连接信息。
4. 增加"配置最小集"启动自检（health/readiness 探针依赖它，见缺失项）。

### 4. 【高】网关的路由持久化落在 `AppContext.BaseDirectory`：每副本一份文件、仓库里的 `proxy-config.json` 是死文件、文档格式与模型不一致

**证据**

- 存储路径硬编码为输出目录：`Core\Gateway\JsonProxyConfigStore.cs:44` `_configPath = Path.Combine(AppContext.BaseDirectory, "proxy-config.json");`
- 仓库里那份 `Host\NexusStack.Gateway\proxy-config.json`（内容 `{"routes": [], "clusters": []}`）**不在任何 csproj 的 Content 里**：`Gateway\NexusStack.Gateway.csproj:12-19` 只有 PackageReference/ProjectReference。全仓 `grep proxy-config` 仅命中 `JsonProxyConfigStore.cs:44` 与 README。所以运行时它并不存在，`EnsureConfigFileExists()` 会在 `bin/`（或容器 `/app`）里新建一个空文件：`JsonProxyConfigStore.cs:47`、`:374-384`。
- `EnsureConfigFileExists()` 在 `try/catch` **之外**调用（`:47` 早于 `:50` 的 try），容器里只读根文件系统或非 root 用户下会直接抛出，DI 解析失败。
- 写文件不是原子写（没有 temp+rename）：`JsonProxyConfigStore.cs:177-178` `await File.WriteAllTextAsync(_configPath, json)` —— 进程在写入中途被杀会留下截断的 JSON，而 `LoadConfigAsync` 的读取重试只处理 `IOException`（`:148-154`），`JsonException` 会被 `:155-159` 吞掉并返回**上一份缓存/空配置**。
- 路由存在进程外的**单文件、单副本**，没有任何共享存储或多副本同步：文件监听（`:294-308`）只对本机文件生效。
- 文档与实现不一致：`README.md:411-434` 给出的示例是 `{"Routes":[{"Match":{"Path":"/api/{**catch-all}"}}], "Clusters": {"api-cluster": {...}}}`（`Match` 嵌套、`Clusters` 为字典），而模型是扁平 `Path` + `Clusters` 数组：`Core\Gateway\ProxyRouteConfig.cs:9-11`、`Core\Gateway\ProxyConfiguration.cs:9-10`、`ProxyClusterConfig.cs:10`。

**影响**

（a）容器重启/滚动发布后手工加的路由全部丢失（除非挂卷，而 Dockerfile 没声明 VOLUME）；（b）多副本网关各自维护一份路由，同一个路由在不同副本上生效状态不同，客户端的 503/404 变成间歇性；（c）`proxy-config.json` 这个"配置入口"其实是死文件，运维按 README 放进去的配置完全不生效，且 README 的格式即使生效也解析不出 `Path`/`ClusterId`；（d）非原子写在崩溃场景下丢配置。

**建议**

1. 把 `IProxyConfigStore` 换成服务端存储（数据库表 `GatewayRoute`/`GatewayCluster`，或 etcd/Consul/Redis+版本号），并在路由模型上引入 `Version`/`RowVersion` 做乐观并发；JSON 文件仅保留为本地开发实现（`IProxyConfigStore` 接口本身是好设计，直接换实现即可）。
2. 写入改为 temp + `File.Move(overwrite: true)` 原子替换；读取时区分"格式错误"（应 fail fast 并保留 last-known-good + 上报健康状态）与"文件被占用"（可重试）。
3. 启动早期做一次配置加载并把结果计入 readiness；把 `EnsureConfigFileExists` 移入 try 或改为"目录不可写则报明确错误"。
4. 修正 README 的示例格式，或反过来给模型加 `Match`/`Clusters` 的兼容层；同时把 `proxy-config.json` 用 `<Content CopyToOutputDirectory="PreserveNewest" />` 显式纳入项目，避免"文件在仓库里但运行时不存在"的错觉。

### 5. 【高】网关是裸转发：无认证、无限流、无关联 ID、无重试/熔断/超时/被动健康检查；且"reload 成功"会在配置无效时撒谎

**证据**

- 边缘能力缺失：网关分支只注册了 `AddAuthorization()`、锁服务、配置存储和 `AddReverseProxy`（`Core\ServiceCollectionExtensions.cs:199-223`），`UseApp` 里对网关只多了 `MapReverseProxy()`（`:318-322`）。全仓 `grep` 无 `AddRateLimiter|UseRateLimiter|AddResilience|Polly|AddOutputCache|ResponseCache|AddApiVersioning|OpenTelemetry`（0 命中）。
- 路由/集群模型**没有承载策略的字段**：`ProxyRouteConfig.cs:7-16` 只有 `RouteId/ClusterId/Path/Headers/Methods/Order/Metadata`，没有 `Transforms`、`AuthorizationPolicy`、`CorsPolicy`、`RateLimiterPolicy`、`TimeoutPolicy`；`ProxyClusterConfig.cs:16-29` 的 `HealthCheckConfig` 只有 `Enabled/Interval/Timeout/Path`（即只支持主动健康检查）。
- 转换函数只映射了部分字段，YARP 的 `RouteConfig.Transforms`/`ClusterConfig.HttpRequest`（`ActivityTimeout`、`Version`）/`HttpClientConfig`/`SessionAffinity`/被动健康检查全部丢弃：`Core\Gateway\DynamicProxyConfigProvider.cs:102-152`
- 反向代理因此使用 YARP 默认：把所有请求原样转发（客户端的 `Authorization` 头直接透传到后端），默认 100s 活动超时，无重试、无熔断、无每路由限流。
- 配置无校验：`ConvertConfig` 直接 `TimeSpan.Parse(c.HealthCheck.Interval)`（`:141`、`:144`，且未指定 `InvariantCulture`），`RouteId`/`ClusterId`/`Path` 之间没有引用完整性检查；`Path` 是否为合法 YARP 路径模式、目标 `Address` 是否为绝对 URI（`ProxyDestinationConfig.Address`，`ProxyClusterConfig.cs:18`）都未验证。
- **静默失败**：`DynamicProxyConfigProvider.LoadConfigAsync` 把异常吞掉只记日志（`:93-96`），而 `ReloadConfigAsync`（`:40-58`）照常返回；控制器随后返回 `Ok()`：`ProxyManagementController.cs:64-66`（Create）、`:91-93`（Update）、`:113-116`（Delete）、`:163-166`/`:191-194`/`:214-217`（Cluster）、`:239-240`（reload）。

**影响**

（a）网关不是边缘，只是"带管理 API 的透明代理"：后端能被匿名直达，认证分散在每个服务里，无法在边缘做统一鉴权/限流/租户隔离；（b）没有超时与熔断，一个慢后端会通过网关耗尽连接（默认 100s 活动超时 + 无并发限制）；（c）没有被动健康检查与目标剔除，坏实例持续被轮询；（d）运维通过 API 加了一条错的路由（例如 `Interval="5s"` 之外的非法值、或指向不存在的 cluster），接口返回 200 Success，但路由实际没生效，排障成本极高。

**建议**

1. 路由/集群模型补齐 `Transforms`、`AuthorizationPolicy`、`RateLimiterPolicy`、`TimeoutPolicy`、`HttpRequest`(ActivityTimeout/Version)、`HttpClientConfig`、`Passive` 健康检查，并在 `ConvertConfig` 中完整映射（YARP 原生能力，无需自研）。
2. 在网关注册 ASP.NET Core `AddRateLimiter`（按 IP/租户/路由分区）与 `.NET 8+` 的 `AddResilienceHandler`（或 `Microsoft.Extensions.Http.Resilience`）给 YARP 的 `HttpMessageInvoker` 加超时/重试/熔断。
3. 边缘统一认证：网关用自己的方案校验 Bearer，再以 `Transforms` 注入内部身份头（如 `X-User-Id`）并剥离外部 `Authorization`，后端只信任内部头（配合网络策略）。
4. `ReloadConfigAsync` 必须返回结果：把 `LoadConfigAsync` 改为返回 `(bool ok, string? error)`，控制器在失败时返回 422/500 并回滚已写入的配置文件；同时给管理 API 加"试运行校验"（`ValidateOnly`）参数。
5. 关联 ID：在网关生成/透传 `X-Correlation-Id`（或 W3C `traceparent`），写入响应头与日志（见发现 15）。

### 6. 【高】宿主组合是"共享大爆炸"：两个 host 共用一份反射驱动的全栈注册；隐藏耦合把服务焊死在单进程

**证据**

`InitAppliation` → `AddBuilderServices`（`Core\ServiceCollectionExtensions.cs:58-271`）是**唯一**的组合点，其中与宿主能力无关的注册全部无条件执行：`AddHttpContextAccessor`(:116)、`AddSwaggerGen`(:120)、`ConfigureOptions`(:122)、`AddHttpLogging`(:124)、`AddMemoryCache`(:133)、`AddEFCoreAndPostgreSQL`(:144)、`AddLazySupport`(:147)、反射 DI(:151-153)、`AddAliyunOSS`(:155)、CORS(:157-167)、`AddControllers`+4 个全局过滤器(:225-245)、`AddHttpRequestClient`(:248)、`AddAllMapster`(:250)、`AddRabbitMQ`(:252)；`UseApp` 里 `UseDynamicLocalizer`/`ExceptionHandlerMiddleware`/`UseHttpLogging`/`UseRedis`/`UseStaticFiles`/`UseStaticFileServer`/`UseHttpsRedirection`/`UseCors`/`UseAuthentication`/`UseAuthorization`/`MapControllers`/`AddRabbitMQCodeManager` 也全部无条件执行（`:278-327`）。

具体耦合点（都会阻碍"每个微服务一个宿主"）：

| 耦合 | 证据 | 后果 |
|---|---|---|
| 反射式 DI：扫描所有 `NexusStack.*` 程序集，把实现类注册到它实现的**每一个**非泛型接口上 | `Infrastructure\ServiceCollectionExtensions.cs:19-29`、`:36-37`；`Infrastructure\TypeFinders\TypeFinders.cs:74` | 无法按宿主裁剪服务集合；`services.Add` 不去重，注册随程序集数量线性膨胀；一个类实现了 3 个接口就被注册 4 次 |
| 启动时把目录下**所有** `NexusStack.*.dll` 强制加载 | `Core\ServiceCollectionExtensions.cs:340-344` | 每个服务都会加载其他服务的程序集，反射扫描（选项、Mapster、Cron、事件处理器）因此跨服务生效 |
| EF 迁移程序集硬编码为 WebAPI | `Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:40` `pgOptions.MigrationsAssembly("NexusStack.WebAPI")` | 任何新服务的 DbContext 迁移都无处安放；`Host\NexusStack.WebAPI\dotnet-tools.json:1-13` 也把 `dotnet ef` 工具清单放在 WebAPI 目录下 |
| 单一连接串 + 单一 `MainContext` | `Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:21`、`Core\ApiControllerBase.cs` 之下的所有服务直连 `IServiceBase<T>` | 所有数据在一个库、一个上下文，无法按服务切分所有权 |
| 静态服务定位器 | `Core\ServiceCollectionExtensions.cs:280` `App.Init(app.Services)`；`Infrastructure\App.cs:16`、`:34` `ServiceProvider.CreateScope()` | 静态全局状态使宿主无法并存（同一个进程里跑两个 host 会互相覆盖），也让单测难以并行 |
| Redis 客户端全局静态初始化 | `Domain\NexusStack.Redis\ServiceCollectionExtensions.cs:42-43` `new CSRedisClient(...); RedisHelper.Initialization(csredis);` | 每个进程只能有一个 Redis 连接（切换/多租户/测试隔离都变难） |
| 全局过滤器把业务日志发到 MQ | `Core\ServiceCollectionExtensions.cs:237` 注册 `OperationLogActionFilter`；该过滤器在 action 之前 `await publisher.PublishAsync(pushData)`（`Core\Filters\OperationLogActionFilter.cs:85`）；异常过滤器同样发布（`Core\Filters\ApiAsyncExceptionFilter.cs:180`）；`EventPublisher` 构造期同步建 channel（`Domain\NexusStack.RabbitMQ\EventPublisher.cs:34`）且失败会 `throw`（`:98-101`） | MQ 不可用时，**被记录日志的请求会失败**；网关也被同一过滤器覆盖，而网关没有任何 RabbitMQ 配置 |
| 注释掉的宿主服务 | `Core\ServiceCollectionExtensions.cs:262`、`:267` 两处 `AddHostedService` 被注释 | 种子数据/API 资源初始化在 WebAPI 里已经不执行；`InitApiResourceService`/`ExecuteSeedDataService` 成为死代码路径 |

**影响**

"一个解决方案多个可独立部署宿主"目前只是**同一份注册代码换一个 `CoreServiceType` 枚举**。要拆成 N 个服务，必须先把组合拆开，否则每个新服务都会启动即要求全栈依赖；程序集全量加载 + 反射扫描还会让服务之间在类型层面继续互相可见，"微服务"退化成"多进程单体"。

**建议**

1. 组合显式化：删除 `CoreServiceType` 分支式的大函数，改成每个宿主自己写 5-15 行 `AddXxx` 组合（这就是 `Program.cs` 从 8 行变成 30 行的正当理由）；共享的只有"横切关注点"（日志、追踪、异常、认证），其余按需引用。
2. 用 `IServiceCollection` 的模块化包（如每个服务一个 `XxxModule`/`AddXxxModule(this IServiceCollection, IConfiguration)`）替代反射扫描；DI 注册改为 `TryAdd` 并显式列出接口→实现。
3. 把 `MigrationsAssembly` 改为从程序集特性/选项取（`MigrationsAssembly(typeof(XxxContext).Assembly.GetName().Name)`），迁移与 DbContext 同项目；`dotnet-tools.json` 提到仓库根。
4. `App`/`RedisHelper` 静态全局改为注入（`IOptionsSnapshot<T>` + `IConnectionMultiplexer`/`CSRedisClient` 单例），彻底去掉进程级静态状态。
5. 审计日志改为**不阻塞请求**的旁路（发到 `Channel<T>`/后台队列，落盘兜底），MQ 故障不得影响业务响应。

### 7. 【高】`UseHttpLogging` 打开全字段 + 1MB 请求/响应体：登录口令与 Token 会进日志

**证据**

`Core\ServiceCollectionExtensions.cs:124-130`：

```csharp
builder.Services.AddHttpLogging(options =>
{
    options.RequestBodyLogLimit  = 1024 * 1024;
    options.ResponseBodyLogLimit = 1024 * 1024;
    options.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.All;
    options.MediaTypeOptions.AddText("application/json");
});
```

`app.UseHttpLogging()` 在 `Core\ServiceCollectionExtensions.cs:293`（位于 `UseAuthentication` 之前，因此连未认证请求的 body 也记录）。`HttpLoggingFields.All` 包含 `RequestHeaders`（`Authorization`、`Cookie`）、`RequestBody`、`ResponseBody`，而 `AddText("application/json")` 明确把 JSON body 纳入记录范围。WebAPI 的登录端点正好是 JSON body：`Controllers\TokenController.cs:36-40`（`POST api/Token/password`，body 为 `PasswordLoginDto`），修改密码同理 `Controllers\UserController.cs:378-381`。

**影响**

明文口令、Bearer Token、以及全部业务报文进入日志管道。按 `agile\config\nexusdtack_api...cache:1` 的 Serilog 配置，这些日志会写到 `/Logs/NexusStackAPI/log.txt`（`rollingInterval=3`、100MB 上限）并同时进控制台，日志文件因此成为高价值攻击目标；这也违反绝大多数合规要求（PCI-DSS/等保均禁止记录口令与凭证）。

**建议**

1. 生产环境把 `LoggingFields` 收敛为 `RequestPath | RequestMethod | StatusCode | Duration | RequestId`；只在 `Development` 且显式开关时记录 body。
2. 必须记录 body 时配置脱敏：`options.MediaTypeOptions` 用允许列表 + 自定义 `IHttpLoggingInterceptor` 把 `password`/`token`/`secret` 字段替换为 `***`。
3. 把 `Authorization`/`Cookie` 头加入屏蔽列表（`HttpLoggingOptions` 无内置屏蔽，需 `IHttpLoggingInterceptor` 实现）。
4. 日志文件目录做成可挂载卷并限制权限（目前 `/Logs/...` 是绝对路径且容器内无卷）。

### 8. 【高】错误响应统一成 HTTP 200：`RequestJsonResult` 不设置状态码；异常过滤器内部还会再抛异常；最外层中间件不回写响应

**证据**

- `RequestJsonResult` 只是 `JsonResult` 的空子类，从不设置 `StatusCode`：`Infrastructure\Models\RequestJsonResult.cs:11-16`；`JsonResult` 在 `StatusCode == null` 时保持 200。
- 权限过滤器与异常过滤器都用它承载 401/403/500：`Core\Filters\RequestAuthorizeFilter.cs:49`、`:58`、`:67`、`:78`、`:126`、`:136`；`Core\Filters\ApiAsyncExceptionFilter.cs:79` `context.Result = new RequestJsonResult(resultModel);`
- 结果过滤器把一切包装成同样的 200：`Core\Filters\RequestAsyncResultFilter.cs:66`（`StatusCodeResult`）、`:78`、`:96`、`:109`。唯一显式设状态码的是模型校验分支：`:53` `context.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;`
- 异常过滤器内部**在异常处理过程中再次抛异常**：`Core\Filters\ApiAsyncExceptionFilter.cs:108-117`（读表单失败）、`:128-143`（读 JSON 失败时 `throw new InvalidOperationException("Error reading JSON data", ex)`）——这会把原始异常替换成 500，且抛出点位于异常过滤器内部，行为不可预测。
- 最外层中间件只记录、不回写响应：`Core\Middlewares\ExceptionHandlerMiddleware.cs:22-40`（只有 `AuthenticationFailureException` 会写 401，其余分支只 `logger.LogError`），异常逃到这一层时会得到一个**空 body 的 200**。

**影响**

（a）客户端/网关/SDK 无法用 HTTP 状态做重试、熔断、告警，必须解析 body（模板自己的 Swagger 脚本就是这么写的：`nexusstack-swagger.js:157` `if (response.code === 200)`）；（b）未认证访问网关管理 API 返回的是 HTTP 200，任何基于状态码的监控、WAF 规则、压测判定全部失效；（c）异常过滤器二次抛出会掩盖真实错误并可能导致响应"半写"；（d）空的 200 让排障成本进一步升高。

**建议**

1. 统一错误契约时把 HTTP 状态码与 body `code` **同时**设置（401→401、403→403、业务校验→400/409/422、未处理→500），或者明确采用"全部 200 + body code"的私有风格并在网关处转换——两者选一，但不要让边界的 401/500 伪装成 200。
2. 异常过滤器内部不允许再抛：读 body 用 `try { } catch { /* 降级为无 body */ }`，并复用已有的 `EnableBuffering`（`:125`）后再 `Position = 0`；`throw new InvalidOperationException(...)` 两处（`:116`、`:143`）改为记录并继续。
3. `ExceptionHandlerMiddleware` 的非认证分支必须写出 500 + 统一 body（并考虑直接改用 `app.UseExceptionHandler(...)` + `AddProblemDetails`）。
4. 统一响应/异常契约应有契约测试（同一组用例断言 HTTP 状态 + body code）。

### 9. 【高】生产配置里 `ShowStackTrace=True` 与 `Swagger:Enable=True`：堆栈和接口文档对外暴露

**证据**

- 异常过滤器把堆栈写进响应体：`Core\Filters\ApiAsyncExceptionFilter.cs:74-77`
  ```csharp
  if (App.Options<CommonOptions>().ShowStackTrace) resultModel.Data = exception.StackTrace;
  ```
- 生产/测试用的 AgileConfig appId 是 `nexusdtack_api`（`Host\NexusStack.WebAPI\appsettings.Production.json:3`、`appsettings.Test.json:3`），其落地缓存里 `ShowStackTrace = "True"`、`Swagger.Enable = "True"`、`Swagger.SwaggerGen/SwaggerUI = "True"`：`Host\NexusStack.WebAPI\agile\config\nexusdtack_api.agileconfig.client.configs.cache:1`
- Swagger 的启用条件与配置文件无关，只认配置项：`Core\ServiceCollectionExtensions.cs:284-287` `if (app.Environment.IsDevelopment() || ...SwaggerOptions...Enable == true) app.UseSwagger(...)`；而 Swagger UI 与 OpenAPI 文档都挂在普通中间件上（`Domain\NexusStack.Swagger\ServiceCollectionExtensions.cs:84-116`），不经过 `RequestAuthorizeFilter`，因此**匿名可访问** `/docs/index.html` 与 `/api/nexusstack_web_api/swagger.json`。

**影响**

生产环境把服务端堆栈（含文件路径、SQL 片段、内部类型名）返回给任意调用方，为攻击者提供精确的探测信息；同时把完整接口清单与参数结构公开（相当于免费提供攻击面地图）。这是配置与代码双重默认不安全。

**建议**

1. `ShowStackTrace` 只允许在 `Development` 生效（代码里加环境判断，或改用 `IWebHostEnvironment.IsDevelopment() && options.ShowStackTrace`）；生产配置项默认 `False` 并加入启动校验（生产为 True 时拒绝启动）。
2. Swagger 的开关改为 `IsDevelopment() || (Enable && AllowExternalDocsFromConfiguredCidr)`；生产建议完全关闭，或挂到需要认证的 `/admin` 分组后面。
3. 把安全相关开关（`ShowStackTrace`、`Swagger:Enable`、`Authorization:ApiPermissionMode`、`EnableRootBypass`）集中成一个 `SecurityOptions` 并在启动时打印生效值 + 断言生产约束。

### 10. 【中】CORS 依赖一个可能不存在的配置键；转发头信任被清空

**证据**

- `Core\ServiceCollectionExtensions.cs:157-167`：`var cors = builder.Configuration["Cors"] ?? string.Empty;` 然后 `WithOrigins(origins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();`
- WebAPI 的 `appsettings.json:1-24` **没有** `Cors` 键（它只存在于 AgileConfig 缓存里：`nexusstack_api...cache` 中 `Cors = "http://localhost:5173;"`，`nexusdtack_api...cache` 中 `Cors = "http://localhost:3000;"`），网关更是连环境配置文件都没有（发现 3）。配置缺失时 `origins` 为空数组，策略变成"不允许任何来源"，而此时又开启了 `AllowCredentials()`（浏览器禁止 `AllowAnyOrigin` 与 credentials 并存，这组参数本身就是易错组合）。
- 转发头处理清空了所有受信任代理：`Core\ServiceCollectionExtensions.cs:427-435` `ForwardedHeaders = XForwardedFor | XForwardedProto, ForwardLimit = null, KnownIPNetworks.Clear(), KnownProxies.Clear()`；而 `GetRemoteIpAddress` 会直接取 `X-Forwarded-For` 的第一个值：`Domain\NexusStack.Serilog\HttpRequestExtensions.cs:19-31`，该值被写入操作日志 `Core\Filters\OperationLogActionFilter.cs:80`。
- `UseForwardedHeaders` 被放在 `UseStaticFileServer` 内部（`Core\ServiceCollectionExtensions.cs:301` 调用 → `:435` 注册），执行顺序晚于 `UseHttpLogging`（`:293`），因此 HTTP 日志里记录的 scheme/远端地址仍是代理地址。

**影响**

（a）CORS 行为完全取决于外部配置中心是否下发 `Cors`，本地/新环境默认"跨域全挂"，而开发者为绕过它往往直接改成通配 —— 一种典型的"配置缺失 → 不安全默认"路径；（b）网关就是反向代理，清空 `KnownProxies` 后客户端可任意伪造 `X-Forwarded-For` 污染操作审计日志与基于 IP 的风控（登录失败统计、限流都不可信）。

**建议**

1. 显式 CORS 策略：把 `Cors` 改成 `Cors:AllowedOrigins` 数组并在启动时校验（缺失则按环境给默认值或直接失败）；避免 `AllowCredentials + AllowAnyMethod/Header` 的组合，按需列举方法与头。
2. 转发头只信任已知网关：把网关/入口 IP 段写入 `KnownProxies`/`KnownIPNetworks`（或清空后由入口统一注入并剥离外部同名头）；`GetRemoteIpAddress` 只接受来自受信代理的 `X-Forwarded-For`。
3. 把 `UseForwardedHeaders` 提到管线最前（紧跟异常中间件之后），不要藏在静态文件扩展方法里。

### 11. 【中】上传目录被作为公开静态资源暴露：无鉴权、任意文件类型、路径硬编码 `/root/...`，而全仓没有上传入口

**证据**

- `Core\ServiceCollectionExtensions.cs:419-447`：`staticDirectory = Path.Combine(AppContext.BaseDirectory, uploadPath ?? "uploads")`，然后 `Directory.CreateDirectory`，再以 `RequestPath = "/static"`、`ServeUnknownFileTypes = true` 暴露（`:439-444`）。
- `uploadPath` 来自 `Storage:Path`，而 `Host\NexusStack.WebAPI\appsettings.json:20` 是 `"/root/filestorage/uploads"`（绝对路径，`Path.Combine` 会忽略前面的 base dir；Windows 上落到当前盘根 `D:\root\filestorage\uploads`，容器里落到 `/root/...`）。
- 静态文件中间件在鉴权**之前**：`UseStaticFiles()`（`:298`）、`UseStaticFileServer()`（`:301`）都早于 `UseAuthentication()`（`:307`），所以 `/static/**` 是匿名可读的；`ServeUnknownFileTypes = true` 意味着上传的 `.html`/`.svg` 会以原 MIME 提供，可与 API 同源执行脚本。
- 全仓 `grep IFormFile` 只有一处工具方法（`Infrastructure\Utils\FileHelper.cs:10`），**没有任何控制器接收上传**；`DownloadController` 只做下载/列表（`Controllers\DownloadController.cs:22-71`）。
- 下载端点还把整份文件读进内存并转成逗号拼接的字节串返回 JSON：`Controllers\DownloadController.cs:59-68`（`Buffer = string.Join(",", dataByte)`），并硬编码 `FileStorageType.Aliyun`（`:59`），与 `Storage:Type = "Local"`（`appsettings.json:22`）不一致。

**影响**

（a）一旦业务代码写入该目录（模板的 `FileService`/`FileHelper` 就在那里），任何上传内容都可被匿名读取并同源执行 —— 存储型 XSS + 数据泄露；（b）"配置了公开静态目录却没有上传接口"是典型的半成品：目录、OSS、`StorageOptions`、静态映射都齐了，唯独入口缺失，说明这条链路从未端到端跑通；（c）下载接口把二进制塞进 JSON，1MB 文件会膨胀成 3-4MB 字符串并占用大对象堆，是确定的性能陷阱。

**建议**

1. 上传/下载改为受鉴权的专用端点（`POST /api/files` + `GET /api/files/{id}/content`），返回 `FileStreamResult`/预签名 URL；不要用公开静态目录承载用户文件。
2. 若必须静态暴露，改为带签名/过期时间的路径，去掉 `ServeUnknownFileTypes`，加 `Content-Disposition: attachment` 与严格 CSP，并把目录固定在应用目录之内（`Storage:Path` 用相对路径 + 启动校验）。
3. `Storage:Type` 与代码中的硬编码存储类型统一（工厂按 `StorageOptions.Type` 解析，`DownloadController` 不再写死 `Aliyun`）。

### 12. 【中】Swagger 库的内置"接口授权登录"永远登不进去；文档产物与开关是死配置

**证据**

- 脚本把登录请求发到一个**不存在的路由**：`Domain\NexusStack.Swagger\Resources\nexusstack-swagger.js:148` ``fetch(`${nexusstack.host}/api/basic/token/password`, ...)``，而实际路由是 `api/Token/password`（`Core\ApiControllerBase.cs:16` 的 `Route("api/[controller]")` + `Controllers\TokenController.cs:36`），也没有任何控制器映射 `api/basic/...`。
- 脚本给口令加了魔数前缀：`:139` `password: "swagger" + document.getElementById('password').value`，注释说"前端通过 base64 进行转码加密"；但服务端对明文口令做 **Base64 解码**：`Core\Services\Users\UserTokenService.cs:107-111`（`password = password.Base64ToString();`）。脚本既没有做 base64 编码，服务端也不会剥离 `swagger` 前缀 —— 口令哈希必然不匹配，登录 100% 失败。
- Token 存 Cookie 且不带任何安全属性：`nexusstack-swagger.js:5-21`（只拼 `expires`/`path`，无 `Secure`/`HttpOnly`/`SameSite`），并 `console.log('SetToken', token, expireDate)`（`:53`）把令牌打到浏览器控制台；`:118-123` 还给 `window` 加了全局 `keydown` 监听（每次打开弹窗都新增一个，永不移除），在页面任意位置按回车都会触发登录。
- 死配置：`SwaggerOptions.SwaggerGen`/`SwaggerUI`（`Domain\NexusStack.Swagger\SwaggerOptions.cs:20`、`:25`）在代码中**从未被读取**（`AddSwaggerGen` 无条件注册，`UseSwagger` 只读 `Enable`）。
- 文档来源很脆：`Domain\NexusStack.Swagger\ServiceCollectionExtensions.cs:62-63` `Directory.GetFiles(AppContext.BaseDirectory, "*.xml")` 全量 `IncludeXmlComments`，但**没有任何 csproj 设置 `GenerateDocumentationFile`**（全仓 grep 0 命中）—— 结果是把 NuGet 依赖包自带的 XML 注释塞进 Swagger，而项目自己的控制器注释一个都没有。
- 硬编码 OpenAPI 2.0：`Domain\NexusStack.Swagger\ServiceCollectionExtensions.cs:86` `options.OpenApiVersion = OpenApiSpecVersion.OpenApi2_0;`，与现代客户端/代码生成器（OpenAPI 3）不兼容；文档路由也非惯例：`:87` `api/{documentName}/swagger.json`。

**影响**

（a）模板宣传的"Swagger 里直接登录调试"功能不可用，且失败表现为"账号或密码错误"，会浪费使用者大量时间（我按代码判断这是必然失败，不是环境问题）；（b）令牌落入浏览器控制台与无属性 Cookie，在共享机器/截图/日志中泄漏；（c）Swagger 文档内容实际上主要来自第三方包，接口说明缺失，`SwaggerGen/SwaggerUI` 开关误导使用者。

**建议**

1. 登录脚本改为向 `{host}/api/token/password` 发请求并对口令做 base64（与服务端约定一致）；或者更彻底——删掉这段内嵌登录脚本，改用 Swagger UI 原生的 `Authorize` + `AddSecurityDefinition`（`ServiceCollectionExtensions.cs:50-56` 已定义 `Token` 方案），把手动粘贴 token 作为唯一路径。
2. `setToken` 走内存/`sessionStorage` 而不是无属性 Cookie，删除 `console.log(token)`，`keydown` 监听加 `{ once: true }` 或改绑到弹窗元素。
3. 为实现 `GenerateDocumentationFile`（在 `Directory.Build.props` 统一打开）并把 `IncludeXmlComments` 限定为项目自身 XML；`SwaggerGen`/`SwaggerUI` 要么接上要么删除。
4. OpenAPI 版本改回 3.0，文档路径改为 `/swagger/{documentName}/swagger.json` 以便与生态工具默认值对齐。

### 13. 【中】Serilog 库是空壳：真正的日志管道在外部配置中心；配置引用了不存在的 enricher；Seq 只引用了包

**证据**

- 库本身只有 `UseLog`：`Domain\NexusStack.Serilog\ServiceCollectionExtensions.cs:16-36`，全部行为是 `config.ReadFrom.Configuration(context.Configuration)` + 三个自定义 enricher。**没有代码级 sink 兜底**（没有 `WriteTo.Console()`）。
- 而 `Host\NexusStack.WebAPI\appsettings.json:1-24` 与网关 `appsettings.json:1-19` 里**都没有 `Serilog` 节**，日志管道只存在于 AgileConfig：`agile\config\nexusdtack_api...cache:1` 中 `Serilog:Using = ["Serilog.Sinks.Console","Serilog.Sinks.File"]`、`Serilog:WriteTo:1:Args:path = "/Logs/NexusStackAPI/log.txt"`、`rollingInterval = "3"`、`fileSizeLimitBytes = 104857600`。
- 该配置里的 enricher 引用了模板**没有引用**的包：`Serilog:Enrich = ["WithSpan","WithThreadId","WithThreadName"]`，而 `Domain\NexusStack.Serilog\NexusStack.Serilog.csproj:9-16` 只引用了 `Serilog`、`Serilog.Extensions.Hosting`、`Serilog.Settings.Configuration`、`Serilog.Sinks.Console/File/Seq` —— `WithSpan`（`Serilog.Enrichers.Span`）与 `WithThreadId/WithThreadName`（`Serilog.Enrichers.Thread`）都不在其中（全仓 grep `Enrichers` 0 命中）。同时 `OutputTemplate` 引用了 `[{ThreadId}] [{ThreadName}] [{TraceId}]`。
- `Serilog.Sinks.Seq`（`:15`）**从未在任何配置里被使用**（两处缓存里的 `Serilog:Using` 只有 Console/File），README 里的 `Seq` 配置块（`README.md:373-382`）在配置中不存在。
- Enricher 的取值路径有性能问题：每个日志事件都会调 `App.Options<CommonOptions>()`（`Domain\NexusStack.Serilog\WorkerEnricher.cs:19`），而它内部 `CreateScope()` + 解析 `IOptionsSnapshot`（`Infrastructure\App.cs:34-36`）——即**每条日志创建一个 DI scope**。
- `WithToken` 在配置期创建一个**永不释放的 scope** 来拿 scoped 服务：`Domain\NexusStack.Serilog\LoggerExtensions.cs:44` `serviceProvider = serviceProvider.CreateAsyncScope().ServiceProvider;`，靠 `IHttpContextAccessor` 的 AsyncLocal 侥幸做到"按请求"。

**影响**

（a）只要 AgileConfig 不可达或不含 `Serilog` 节，应用**一行日志都不会输出**（`UseSerilog` 收到空配置 = 无 sink），而日志恰是排障的最后手段，这个失败模式极隐蔽；（b）`WithSpan/WithThreadId/WithThreadName` 会让 `Serilog.Settings.Configuration` 在配置绑定阶段找不到方法而抛出（配置一旦下发即启动失败），即便不抛，模板里的 `ThreadId/ThreadName/TraceId` 也永远是空；（c）Seq 依赖是死重量；（d）热路径性能：网关/高吞吐服务下每条日志一个 DI scope。

**建议**

1. 代码里给一个最小可用兜底：`config.WriteTo.Console()`（或 `ReadFrom.Configuration` 之后 `if (!sinks configured) WriteTo.Console()`），保证"配置中心挂了也有日志"。
2. 要么把 enricher 包补进 `NexusStack.Serilog.csproj`（`Serilog.Enrichers.Span`、`Serilog.Enrichers.Thread`），要么把 `Serilog:Enrich` 改成只用自定义/内置 enricher；两者必须一致，并把配置样本纳入仓库（`appsettings.json` 里给出可运行的 Serilog 段）。
3. `WorkerEnricher` 改为构造期注入 `IOptions<CommonOptions>`（`IOptionsMonitor` 也只在变更时读），不要在每个事件里建 scope；`WithToken` 改为 `With<TokenEnricher>()` 并从 `IHttpContextAccessor` 取（`TokenEnricher` 已经这么写了，多余的那次 `CreateAsyncScope` 删掉）。
4. 决定 Seq 的去留：要就配置 sink（含 serverUrl/apiKey 由环境注入），不要就删包，避免"宣称有集中式日志但实际没有"。

### 14. 【中】控制器普遍是薄转发，且把基础设施事实泄漏给调用方；"空控制器"暴露了缺失的领域

**证据**

- 纯转发（控制器方法体 ≤ 3 行、只调用一个 service）：`Controllers\RegionController.cs:27-45`、`:132-139`、`:148-168`、`:176-179`、`:213-249`；`Controllers\ScheduleTaskController.cs:19-67`（三个动作都是 `GetByIdAsync`+改标志位+`UpdateAsync`）；`Controllers\SeedDataTaskController.cs:20-68`（与前者逐行同构）；`Controllers\GlobalSettingController.cs:21-83`；`Controllers\HealthController.cs:18-23`。这类控制器是"浅模块"的教科书样本：接口（HTTP 动词 + DTO）与其实现（一行转发）一样宽，几乎没有隐藏任何复杂度。
- 空壳：`Controllers\PermissionController.cs:8-11`（类体为空，权限操作实际散落在 `RoleController` 与 `MenuController`）；`Controllers\BaseController.cs:9-13` 重复声明基类已有的 `[Route("api/[controller]")]`。
- 泄漏基础设施事实给调用方：
  - `Controllers\OpenAppConfigController.cs:34-44` 把宿主 `IConfiguration` 直接回显给前端（`GetConfigJson` 返回 `Host` 与一个遗留的 `Test` 键），这是调试残留，且把配置键空间暴露成 API；
  - `Controllers\RedisController.cs:17-21` 把 Redis key 扫描（`IRedisService.ScanAsync`）作为公开 API，等于把缓存层当业务接口；
  - `Controllers\UserController.cs:182`、`:232`、`:335` 在控制器里操作 EF 事务与执行策略：`userService.GetDbContext.Database.CreateExecutionStrategy()` + `BeginTransactionAsync`/`RollbackAsync`，业务用例的原子性边界落在 HTTP 层；
  - `Controllers\OpenAppConfigController.cs:58`+`:77` 先 `GetListAsync<AppConfigViewDto>(spec)` 全量取数再 `views.ToPagedList(model.Page, model.Limit)` —— **内存分页**，与 `UserController.cs:78`（`query.ToPagedList`，EF 侧分页）风格相反；
  - `Controllers\MenuController.cs:135` 每次请求都 `apiResourceService.GetListAsync<ApiResourceDto>()` 全表取接口资源再在内存分组（`:149-159`）；
  - `Controllers\AsyncTaskController.cs:65-86` 用 `[FromBody] long id` 传裸标量，并对同一实体连续 `UpdateAsync` 三次（`:77`、`:84`）。

**影响**

（a）空控制器/薄转发说明"宿主层没有自己的职责"——它既不拥有用例编排，也不拥有契约演进，只是 CRUD 的 HTTP 外壳，而这正是缺少领域模型时宿主层必然退化的形态；（b）基础设施泄漏使 API 契约与 Redis/EF/配置实现绑定，任何存储或缓存替换都会破坏对外接口；（c）内存分页在数据量上来后是明确的性能与内存事故点。

**建议**

1. 重建时控制器只做三件事：绑定/校验（最小 DTO）→ 调用一个应用用例（返回结果对象）→ 映射为响应；事务、执行策略、缓存失效、审计一律放在用例内部（`UserController` 的事务应下沉到 `UserAppService.CreateUserAsync`）。
2. 删除 `GetConfigJson`、`RedisController` 这类"宿主自省"端点（若确需，放进独立的、需运维权限的 `/admin/diagnostics`）。
3. 统一分页：所有列表端点走同一个 `PagedRequest/PagedResult<T>` 契约并在 EF 侧分页（把 `ToPagedList` 换成 `GetPagedListAsync`，参考 `Controllers\RoleController.cs:67`、`Controllers\DownloadController.cs:48`）；把分页上限（`Limit` 最大值）做成服务端强制约束。
4. 用"按用例分包"替代"按实体建控制器"：一个限界上下文一个 Controller（或最小 API 分组），避免 `RoleController`+`MenuController`+`PermissionController` 三家共同拥有权限用例。

### 15. 【中】横切关注点盘点：只有一个全局响应包装器（承担 4 件事）和一个条件性的 `X-TraceId`

**证据**

- 统一响应包装、分页元数据、400 归一、TraceId 头四项职责全在同一个过滤器里：`Core\Filters\RequestAsyncResultFilter.cs:31-34`（`X-TraceId`）、`:43-55`（400 归一）、`:56-67`（`StatusCodeResult` 包装）、`:82-97`（`IPagedList` → `RequestPagedResultModel`）、`:100-110`（普通 `ObjectResult` 包装）。
- `X-TraceId` 只在 `Activity.Current != null` 时写出（`:31-33`），而全仓没有任何 `OpenTelemetry`/`ActivitySource`/`AddSource` 配置（grep 0 命中）——是否有 `Activity` 取决于运行时是否挂了监听器，因此这个头**时有时无**；校验失败（400 分支会在 `:53` 直接设状态码后继续走包装）与 `FileStreamResult` 等非 `ObjectResult` 路径也不会被补上。
- 入站关联 ID 完全没有处理：没有任何地方读取 `X-Correlation-Id`/`X-Request-Id`/`traceparent`，网关也不注入；日志侧对应的 `{TraceId}` 依赖发现 13 里缺失的 `WithSpan`。
- 请求日志＝`UseHttpLogging`（发现 7）；没有 `Serilog` 的 `UseSerilogRequestLogging`（全仓 0 命中），因此没有"一条一请求、带耗时/状态码/路径"的结构化访问日志。
- 校验：全仓无 FluentValidation，`[Required]` 0 命中，只有少量 `[MaxLength]`（如 `Domain\NexusStack.Core\Dtos\Users\CreateUserDto.cs:15`），且存在文案与限值不符：`Dtos\Menus\CreateMenuDto.cs:18` `[MaxLength(256, ErrorMessage = "菜单名称不能超过 64 个字符")]`；业务校验靠控制器里手写 `throw new BusinessException("...")`（如 `Controllers\UserController.cs:171-176`）。没有一处读 `ModelState`。
- 幂等：仅消费侧有（`Domain\NexusStack.RabbitMQ\EventSubscriber.cs:540` 的 `TryAcquireIdempotencyAsync`，开关在 `RabbitOptions.cs:67`），**入站 HTTP 没有幂等键**。
- 缓存头：`ResponseCache`/`OutputCache`/`ETag` 0 命中；`HealthController` 只有 `GET api/Health/healthTips` 返回常量 `999`（`Controllers\HealthController.cs:18-23`），无 `AddHealthChecks`/`MapHealthChecks`。
- 优雅停机：没有 `HostOptions.ShutdownTimeout`、没有 `IHostApplicationLifetime`/`ApplicationStopping` 处理（grep 0 命中），YARP 也没有 drain 窗口；网关的 `JsonProxyConfigStore.Dispose`（`Core\Gateway\JsonProxyConfigStore.cs:386-427`）会在停机时强锁 1 秒。

**影响**

（a）跨服务追踪无法建立：一次调用链在网关处断掉，日志里既无共享 trace id 也无可传递的关联 id，微服务化后基本无法排障；（b）统一响应契约与分页元数据混在一个过滤器里，任何契约调整都会牵动分页与 400 行为（改一处、动三处）；（c）健康检查形同虚设，无法接入 K8s/负载均衡的 readiness；（d）无入站幂等，配合"客户端超时重试"很容易产生重复扣减/重复建单；（e）优雅停机缺失会在滚动发布时把在途请求变成 502。

**建议**

1. 关联 ID 中间件：优先透传入站 `traceparent`/`X-Correlation-Id`，缺失则生成，写入 `Activity`/日志作用域/响应头，并由网关保证跨服务传递；同时启用 OpenTelemetry（`AddOpenTelemetry().WithTracing(...)` + OTLP 导出）让 `Activity.Current` 稳定存在。
2. 用 `app.UseSerilogRequestLogging()`（或自研 `RequestLoggingMiddleware`）产出一请求一条的结构化日志，替代/收敛 `UseHttpLogging` 的全字段模式。
3. 拆分响应包装：一个过滤器只做"契约包装"，分页元数据由一个显式的 `PagedResult<T>` 类型承载（让返回值本身就带元数据，而不是靠过滤器嗅探 `IPagedList`）。
4. 接入 `AddHealthChecks()` + `AddNpgSql/AddRedis/AddRabbitMQ` + `MapHealthChecks("/healthz")`（liveness）与 `/readyz`（readiness，含配置加载与 Redis/MQ 依赖），替换返回 `999` 的假健康检查；网关用它做目标剔除。
5. 入站幂等：为写操作引入 `Idempotency-Key` 头 + Redis 去重（模板已有 Redis，成本很低）。
6. 显式设置 `HostOptions.ShutdownTimeout`，在 `ApplicationStopping` 中先让网关停止接受新连接（YARP 的 destination/健康检查置空）再退出。

### 16. 【中】`OperationLogController` 50 行查询逻辑复制两份且导出无上限；`UserController` 改密做 N 次更新

**证据**

- `Controllers\OperationLogController.cs:26-74`（列表）与 `:84-132`（导出）是逐行重复的谓词构造 + 投影查询；导出走 `query.ToList()` 后整表进内存（`:132-135`），没有 `Take`/时间范围硬约束。
- `Controllers\UserController.cs:384-397` 修改口令时逐条 `UpdateAsync` 每个未过期 Token，并在循环里逐条 `redisService.DeleteAsync`（N 次数据库写 + N 次 Redis 往返）；`Controllers\MenuController.cs:247-252` 的 `InvalidateMenuUsersAsync` 也是 `foreach ... await InvalidateAsync`。
- 列表端点普遍返回 `IPagedList<T>`（`Controllers\UserController.cs:37`、`OperationLogController.cs:24`、`TokenLogController.cs:27`、`GlobalSettingController.cs:22`）而另外一些返回 `IEnumerable`/`List`（`Controllers\AsyncTaskController.cs:36` 虽然内部取了分页却声明为 `IEnumerable<AsyncTaskDto>`，`ScheduleTaskController.cs:20`、`SeedDataTaskController.cs:21`、`RegionController.cs:27`）——分页元数据是否返回取决于返回类型的巧合。

**影响**

（a）重复代码会让"日志筛选条件"在两个端点之间漂移（示例：`Keyword` 分支中 `filter.Or(...)` 的用法在列表与导出里语义不同：`:28-32` vs `:86-90`），审计导出与页面看到的数据可能不一致；（b）导出无上限是内存/OOM 风险，也是"导出接口被当数据抽取入口"的安全风险；（c）逐条更新在批量场景（一个用户 10 个 Token、一个菜单 100 个角色用户）会把一次操作放大成上百次往返。

**建议**

1. 把日志查询抽成一个方法（`BuildOperationLogQuery(model)`），列表与导出共用；导出加服务端上限（如 ≤ 5 万行）+ 超出时改为异步任务（模板已有 `AsyncTask`/`DownloadItem` 设施，正好用上）。
2. 批量更新：`ExecuteUpdateAsync`/`BatchUpdate` + Redis `DeleteAsync(string[])` 批量删除；缓存失效改为"版本号/代际递增"而非逐用户删除。
3. 统一分页契约（返回 `PagedResult<T>`）并把 `Limit` 上限、排序白名单统一在一处。

### 17. 【中】`NexusStack.Excel`：反射式模板函数可调用任意 `ReportHelper` 方法、`O(n²)` 的 `ToDataTable`、列字母算法 >26 列即错、PDF 是教程残留、许可证风险

**证据**

- 模板函数用反射按名字调用：`Domain\NexusStack.Excel\Export\ExcelExtensions.cs:48-55` `var method = typeof(ReportHelper).GetMethod(functionName); ... return method.Invoke(null, parameters);`，`functionName` 来自模板文本 `{{#FuncName(...)}}`（`ExcelExtensions.cs:10,23-33`）。没有允许列表；未找到方法时 `method` 为 null → `NullReferenceException`。
- 模板路径直接来自调用方（`ExcelTemplateRender.cs:15-19`、`EPPlusExtensions.cs:18/24`），没有根目录约束 —— 只要模板路径能来自上传/配置，就是**任意文件读取**入口；若模板内容可控，则配合反射是**任意静态方法调用**（当前 `ReportHelper` 只暴露无副作用方法，风险取决于后续往该类加什么）。
- `O(n²)`：`Domain\NexusStack.Excel\ExcelHelper.cs:30-42` 在 `for (i < collection.Count())` 循环里反复 `collection.Count()` 与 `collection.ElementAt(i)`，对 `IEnumerable`（尤其未物化的 LINQ 序列）是指数级重枚举。
- 列字母用 `char` 算术，>26 列产生非法列标：`Domain\NexusStack.Excel\Export\EPPlusExtensions.cs:53-57` 与 `:192-196` `char colEnd = (char)(colStart + (count - 1)); ... "A1:{colEnd}1"` → 27 列得到 `A1:[1`，合并单元格直接抛异常。
- `PDFHelper` 是 iTextSharp 教程样例残留且必然崩：`Domain\NexusStack.Excel\PDFHelper.cs:27` `string poath = "~/fonts/CALIBRI.TTF"`（不是文件系统路径）、`:31` `new FileStream("~/TempPdf" + "\\" + "download.pdf")`（Windows 分隔符 + 波浪号目录）、`:37-41` 留着示例作者 "Mikael Blomquist" / "Sample application using iTestSharp"、`:49` `cb.BeginText()` 之后没有 `EndText()`、`:51` `Image.GetInstance("~/images/arg.png")`；`CreatePdfInvoice` 里 `:80-81` `Path.GetFullPath("images") + "\\logo.png"` 在 Linux 容器里必然 `FileNotFoundException`；`getOrderPDFHeight` 忽略入参直接返回 `1000f`（`:117-120`）、`getDeliveryPDFHeight`（`:122-125`）无人调用。
- 许可证：EPPlus 8 在商业产品里必须设置商业许可，而代码设置的是非商业（`Export\EPPlusExtensions.cs:37`、`:177`、`ExportStream\ExcelEncryptHelper.cs:24` 都写 `LicenseContext.NonCommercial`），而 `Core\ServiceCollectionExtensions.cs:60` 里 `ExcelPackage.LicenseContext = LicenseContext.Commercial;` 被注释掉了。
- 三套引擎并存：EPPlus（导出/加密）、NPOI（`ExportStream\ExportExcelHelper.cs:23` 生成 xlsx）、iTextSharp（PDF）；宿主只用到两个静态方法：`Domain\NexusStack.Core\Services\SystemManagement\OperationLogService.cs:129`（`ExportExcelHelper.ExportToExcel`）与 `DownloadService.cs:60`（`ExcelEncryptHelper.EncryptExcel`）。其余（`ExcelHelper`、`ExcelReader`、`EPPlusExtensions`、`ExcelTemplateRender`、`ExcelExtensions`、`PDFHelper`、`Statement`、`SheetDto`、`PDFOrderInvoiceDto`）在仓库内**无任何调用点**（grep 仅命中定义文件自身）；`Domain\NexusStack.Core\Services\SystemManagement\RegionService.cs:9` 的 `using NexusStack.Excel.Export;` 是未使用 using。
- `ExcelReader` 以文件路径而非 Stream 工作（`Import\ExcelReader.cs:80` `new ExcelPackage(this.File.FullName)`），且异常处理是 `throw new Exception(ex.Message)`（`:174-177`）——丢失堆栈，与模板里其它异常类型（`BusinessException`/`ErrorCodeException`）也不统一。

**影响**

（a）反射式模板语言一旦被上传模板触发，就是远程代码执行/任意文件读取的种子；这是"深模块但边界未加固"的典型；（b）`O(n²)` 与非法列标会在真实报表（几十列、几万行）上直接崩或超时；（c）`PDFHelper` 是死代码但在 `Domain` 库中，会让重建者误以为存在 PDF 能力；（d）EPPlus 非商业许可用在商业模板里是法律风险。

**建议**

1. 模板函数改为**显式注册表**（`Dictionary<string, Func<object?[], object?>>`），禁止反射 `GetMethod`；模板路径限定在 `AppContext.BaseDirectory/templates` 之下（`Path.GetFullPath` + 前缀校验）。
2. `ToDataTable` 先 `ToList()`/用 `IReadOnlyList<T>`，列字母改用 EPPlus 的 `ExcelAddress`/`Cells[row, col]` API 而不是拼字符串。
3. 删除 `PDFHelper`（或重写为 Stream→byte[] + 跨平台字体路径 + 中文 BaseFont）；`getOrderPDFHeight` 若不需要动态高度就从签名里去掉入参。
4. 决策 Excel 引擎：保留 NPOI（导出，Apache-2.0）或购买 EPPlus 商业授权并在**一处**设置 `LicenseContext`，不要三套并存。
5. 未使用的 800 行要么移入 `Samples/`，要么删除；`ExcelReader` 改 Stream 入口并把异常包装成业务异常。

### 18. 【中】Dockerfile 与实际发布产物路径、`.dockerignore` 三者互相矛盾——按现状构建必然失败

**证据**

- `Host\NexusStack.WebAPI\Dockerfile:8-10` 与 `Host\NexusStack.Gateway\Dockerfile:9-11` 都只有 `COPY publish/ ./` + `ENTRYPOINT`，且都注释着"用于在快速模式(默认为调试配置)下从 VS 运行时"——这是 VS 容器工具的**调试用** Dockerfile，不是可复现构建：没有 `sdk` 阶段、没有 `dotnet restore/build/publish`。
- `.dockerignore:38` 排除了 `**/publish`（按 Docker 的 ignore 语义，`**/xxx` 连同**根目录**的 `xxx/` 一并排除）。Docker 在把构建上下文交给 daemon 前先应用 `.dockerignore`，因此 `COPY publish/ ./` 的源目录在上下文里不可见 → `COPY failed: no source files were specified`。
- 即使手工预先生成 `publish/`，路径也对不上：WebAPI 的发布配置输出到 `bin\Release\net10.0\publish\`（`Properties\PublishProfiles\FolderProfile.pubxml:11`），而 `COPY publish/` 期望的是项目目录（WebAPI，未设 `DockerfileContext`）或仓库根（Gateway：`NexusStack.Gateway.csproj:9` `<DockerfileContext>..\..</DockerfileContext>`）下的 `publish/`。
- `.dockerignore:22` 还排除了 `**/appsettings.*.json`，即镜像里**只有** `appsettings.json`：本意是防止密钥进镜像，但当前 `appsettings.json` 里恰好放着 `/root/filestorage/uploads` 这类路径（`appsettings.json:20`），而 AgileConfig 参数全在 `appsettings.Development.json:2-12` —— 生产镜像没有环境文件、也没有 AgileConfig 参数（见发现 3），只能靠环境变量，这与 README 的部署说明（`README.md:355-407`，让用户改 appsettings）互相矛盾。
- 容器里没有 `USER`（以 root 运行）、没有 `HEALTHCHECK`、没有 `VOLUME`（日志 `/Logs/...`、上传 `/root/filestorage/uploads` 与 `AppContext.BaseDirectory/proxy-config.json` 都不持久化）、没有 `docker-compose`/k8s 清单、`.github` 目录为空（无 CI）。

**影响**

"开箱即可 Docker 部署"的承诺不成立：`docker build` 第一步就失败；即使修好 `COPY`，也会得到"以 root 运行、日志与路由不持久、无健康检查、无编排"的镜像。对于宣称"Docker ready"的模板，这是文档与实现的双重失真。

**建议**

1. 改用标准多阶段 Dockerfile：`mcr.microsoft.com/dotnet/sdk:10.0` → `dotnet restore/publish -c Release -o /app/publish` → `aspnet:10.0` 运行时 + `USER $APP_UID` + `HEALTHCHECK`（打 `/healthz`）+ `VOLUME`/明确的挂载点说明。
2. `.dockerignore` 的 `**/publish` 保留（这是对的），但要让它与 Dockerfile 的构建方式一致（多阶段构建不再依赖预生成的 `publish/` 目录）。
3. 提供 `docker-compose.yml`（WebAPI + Gateway + postgres + redis + rabbitmq + agileconfig + seq）作为唯一可运行示例，并让 README 的部署章节改为"compose up"，而不是让使用者手工改 5 份 appsettings。
4. 在 CI（目前 `.github` 为空）加 `dotnet build` + `docker build` + 启动冒烟测试，让"模板可构建/可启动"成为门禁。

### 19. 【低】模板残留与死文件；`HealthController` 之外的"健康检查"不存在

**证据**

- `.http` 文件仍是模板脚手架：`Host\NexusStack.WebAPI\NexusStack.WebAPI.http:1-4` 里变量名是 `MineGuard.WebAPI_HostAddress`（模板明显派生自另一个项目 "MineGuard"），请求的是 `/weatherforecast/`（`dotnet new webapi` 样例端点，本仓库不存在）；`NexusStack.Gateway.http:3` 同样是 `/weatherforecast/`。
- `wwwroot\index.html:1-10` 是空页面，而 `UseStaticFiles()`（`Core\ServiceCollectionExtensions.cs:298`）会把它作为 `/` 的响应返回。
- 健康检查只有常量：`Controllers\HealthController.cs:18-23` 返回 `999`；全仓无 `AddHealthChecks`/`MapHealthChecks`（grep 0 命中）。
- 模块标识三处不一致：`nexusstack_web_api`（`Program.cs:3`）、`nexusstack-gateway`（`Gateway\Program.cs:4`）、`nexusstack-mq`（`BackgroundServices\NexusStack.MQService\Program.cs:5`），Swagger 文档名因此是 `api/nexusstack_web_api/swagger.json`（带下划线）——`Domain\NexusStack.Swagger\ServiceCollectionExtensions.cs:87` 直接把它当路由段用。
- 两个解决方案文件并存：`NexusStack.sln` 与 `NexusStack_Backend.slnx`，而 `.template.config\template.json:15-22` 把两者都列为 `primaryOutputs`。

**影响**

使用者会先踩到"点一下 .http 就 404"和"访问根路径是白页"的小坑，损害模板可信度；模块命名不一致会让日志/文档/配置中心的 key 无法统一（例如按 moduleKey 过滤日志时 `nexusstack_web_api` 与 `nexusstack-gateway` 风格不一）。

**建议**

1. 删除/改写 `.http`（给出真实的登录 + 一个业务请求示例，这是模板最好的"使用说明"），`wwwroot/index.html` 换成一句话的服务信息页或直接删掉静态文件中间件。
2. 统一模块命名为 kebab-case（`nexusstack-webapi`），并把它作为日志属性/追踪 service.name/配置中心 tag 的唯一来源。
3. 保留一个 sln（`.slnx`）+ 一份 `Directory.Build.props`，避免两套工程定义漂移。

### 20. 【低】`Route` 特性重复声明导致路由前缀靠继承叠加，读者无法从单个文件看出真实路径

**证据**

`Core\ApiControllerBase.cs:16` 已有 `Route("api/[controller]")`；`Host\NexusStack.WebAPI\Controllers\BaseController.cs:9` 再次声明同样的 `Route("api/[controller]")`；`Host\NexusStack.Gateway\Controllers\BaseController.cs:9` 又在基类之上叠加 `Route("api/gateway/[controller]")`。网关控制器因此与基类模板的关系只能靠"特性继承叠加"的规则推断，而这个规则在模板里既没注释也没测试。

**影响**

可读性与可预测性下降：新增控制器时"到底挂在 `/api/x` 还是 `/api/gateway/x`"需要读三层基类；路由前缀的变更也容易只改一处而漏掉另一处。相比之下，权限校验依赖**路由模板字符串**（`Core\Filters\RequestAuthorizeFilter.cs:120-131` 用 `routeTemplate:HTTPMETHOD` 作为权限键），前缀一旦意外变化，权限键也会变化（缓存里的 `ApiResource.RoutePattern` 随之失配）。

**建议**

只在 `ApiControllerBase` 声明一次 `[Route("api/[controller]")]`；需要额外前缀的宿主用 `[Route("api/gateway/[controller]")]` 明确覆盖（并加注释说明覆盖关系），或改用 `MapControllers()` 的路由前缀分组/`IEndpointRouteBuilder` 分组来表达。

---

## 值得保留

以下内容在重建中应直接保留或只需换实现，不要重写：

1. **YARP 动态配置的抽象**：`IProxyConfigStore`（`Core\Gateway\IProxyConfigStore.cs:7-17`）把"路由/集群的读写"与"配置的通知/消费"分开，`DynamicProxyConfigProvider` 只负责 `IProxyConfig` 的构建与变更通知。这是本区域内**最深**的模块之一：接口很小（8 个方法），实现细节（缓存、文件监听、防抖、并发）全部隐藏。重建时保留接口、替换存储实现即可。
2. **配置变更通知与旧配置延迟释放**：`DynamicProxyConfig` 用 `CancellationChangeToken` 通知 YARP，并在 `LoadConfigAsync` 里先 `SignalChange()` 再延迟 1s 释放旧配置（`Core\Gateway\DynamicProxyConfigProvider.cs:77-88`、`DynamicProxyConfig.cs:15-47`）——这是少数几个真正处理了"订阅者持有可能正在释放的对象"这一微妙问题的代码。
3. **基于路由模板的 O(1) 权限判定**：`Core\Filters\RequestAuthorizeFilter.cs:113-138` 用 `routeTemplate:METHOD` 作为键从预计算的 `ApiPermissionKeys` 集合里查，避免了每次请求查库；注释里解释了"用路由模板而非 ActionName 以区分同名重载"（`:114-115`）。这个思路（预计算权限集合 + 命中缓存）在微服务里可以直接沿用。
4. **多平台用户上下文缓存 + 精准失效**：认证阶段一次性构建 `UserContextCacheDto` 放进 `HttpContext.Items`（`Core\Authentication\RequestAuthenticationTokenHandler.cs:46-48`），并采用平台维度缓存（`GetOrSetAsync(userId, platformType)`）；在角色/菜单/用户变更后按影响面失效（`Controllers\UserController.cs:278`、`RoleController.cs:146-153`、`MenuController.cs:100-101`、`:215-216`）。"写操作后让谁失效"在模板里是被认真想过的。
5. **IP 归一化工具**：`Domain\NexusStack.Serilog\HttpRequestExtensions.cs:17-45` 处理了 `X-Forwarded-For` 多值、IPv4-mapped IPv6 映射；只需在此基础上加"仅信任已知代理"。
6. **Excel 的 Stream 化导出与加密**：`Domain\NexusStack.Excel\ExportStream\ExportExcelHelper.cs:20-78`（`IEnumerable<T>` + 列映射 → `byte[]`，枚举描述自动本地化，不落盘）与 `ExcelEncryptHelper.EncryptExcel`（`:19-37`，纯 `byte[] → byte[]`）是两个边界干净、无副作用的纯函数模块，直接可用。
7. **审计日志的事件化**：`OperationLogEventData` + `IEventPublisher` 的意图（请求日志与业务解耦、由后端统一落库）是对的；问题只在同步等待与 MQ 依赖（见发现 6），改成后台队列即可保留设计。
8. **每环境一份 appsettings + AgileConfig 按 `env`/`tag` 分环境的思路**（`appsettings.{Development,Staging,Test,Production}.json`）：方向正确（配置外置 + 环境隔离），需要修的是密钥管理与"缺失即失败"的策略。
9. **Swagger 资源内联的工程手法**：把 CSS/JS 作为嵌入资源内联进 `HeadContent`（`Domain\NexusStack.Swagger\ServiceCollectionExtensions.cs:107-115`）避免了静态文件路径问题，这个手法本身可复用（内容需重写，见发现 12）。
10. **YARP 主动健康检查已被模型化**：`ProxyClusterConfig.HealthCheckConfig`（`ProxyClusterConfig.cs:23-29`）→ `HealthCheckConfig.Active`（`DynamicProxyConfigProvider.cs:134-147`）已经打通了配置到 YARP 的路径，补充字段即可获得目标剔除能力。

---

## 缺失项

按"生产级边缘 + 宿主"应有的能力逐项对照，以下均**未实现**（附最小验收标准）：

**边缘（网关）**

- **认证/授权**：无边缘认证方案（发现 2）；验收标准——匿名请求在网关被拒（401）且不转发到后端，内部头由网关注入并剥离外部同名头。
- **限流/防刷**：无限流（grep `AddRateLimiter` 0 命中），登录端点也没有验证码/失败锁定（`CaptchaDto` 存在但无控制器使用）；验收——按 IP/租户/路由分区的限流，超限 429 且与后端解耦。
- **韧性**：无重试/熔断/超时/并发隔离（无 Polly/`Microsoft.Extensions.Http.Resilience`），YARP 默认 100s 活动超时；验收——每路由可配超时 + 失败实例熔断 + 半开探测。
- **负载均衡与目标发现**：`LoadBalancingPolicy` 字段可配（`ProxyClusterConfig.cs:11`），但目标地址是静态列表，没有接入 AgileConfig 的 `ServiceRegisterInfo`（该字段只在 `appsettings.*.json:12-15` 被声明、被打印，从未用于解析 destination）；验收——destination 由服务注册表/`IProxyConfigProvider` 动态解析，实例上线/下线自动生效。
- **被动健康检查与目标剔除**：只有主动检查模型；验收——连续失败 N 次剔除，恢复后自动回池。
- **关联 ID / 分布式追踪**：无（发现 15）；验收——跨网关与后端的同一 trace id 出现在日志与响应头。
- **安全响应头/传输**：无 HSTS（`UseHsts` 0 命中）、无 CSP/X-Content-Type-Options，`UseHttpsRedirection`（`:303`）在容器里没有已知 HTTPS 端口时是空操作；验收——TLS 终止策略明确 + 安全响应头基线。
- **请求体/大小限制与超时**：无 `MaxRequestBodySize`/`RequestTimeouts` 配置；验收——按路由设置请求体与超时上限。
- **BFF 关注点**：无（无聚合端点、无响应裁剪、无前端会话）；验收——若前端需要聚合，独立一层 BFF，而不是让 API 兼做。

**宿主与集成**

- **服务发现注册**：`serviceRegister.serviceId/serviceName` 存在但没有任何注册调用（AgileConfig 的 `RegisterCenter` 只被 `using` 进来，`Core\ServiceCollectionExtensions.cs:42`）；验收——服务启动自注册、优雅下线注销。
- **配置契约与校验**：无 `ValidateOnStart`/Options 校验（`ConfigureOptions` 用反射绑定、失败仅 `Console.WriteLine`，`Core\ServiceCollectionExtensions.cs:518-535`）；验收——必需配置缺失时启动失败并给出键名。
- **密钥管理**：无 User Secrets/Vault/Docker secrets/k8s Secret 的接线示例，且仓库内已有明文（发现 1）；验收——所有敏感值仅来自环境/密钥服务，仓库零密钥。
- **契约与版本策略**：无 API 版本化（`AddApiVersioning` 0 命中），OpenAPI 硬编码 2.0（发现 12）；验收——版本策略（URL 或头）+ 兼容性规则 + 契约产物进 CI。
- **可观测性**：无 metrics/`/metrics`、无 EventCounters、无可用的追踪导出，Seq 仅存在于包引用（发现 13）；验收——请求量/延迟/错误率/依赖健康可被外部系统采集。
- **入站幂等**：无（发现 15）；验收——写操作支持 `Idempotency-Key` 且重放返回同一结果。
- **健康与就绪**：无 `AddHealthChecks`/`MapHealthChecks`，`HealthController` 返回常量（发现 19）；验收——`/healthz`（存活）与 `/readyz`（依赖就绪）分离，并接入编排探针。
- **优雅停机与发布**：无 `ShutdownTimeout`、无 drain、无迁移/发布策略；验收——滚动发布零 5xx，网关先摘流量再退出。
- **每服务数据所有权**：单一 `MainContext` + 单一连接串 + 硬编码 `MigrationsAssembly("NexusStack.WebAPI")`（发现 6）；验收——每个服务独立 schema/库 + 独立迁移程序集，跨服务只经 API/事件。
- **编排与 CI**：无 compose/k8s 清单、无 `.github/workflows`（目录为空）、无冒烟测试；验收——一条命令拉起依赖，CI 完成"构建 + 启动 + 健康检查"。
- **测试**：全仓无任何测试工程（`Microsoft.NET.Test`/xunit/NUnit/MSTest 0 命中）；验收——至少覆盖"鉴权短路、错误契约、网关路由增删、配置加载失败"四类用例（本报告发现 2/5/8 的缺陷若有一行测试都不会存在）。

---

## 风险清单

| 严重度 | 问题 | 位置 |
|---|---|---|
| 严重 | 模板打包包含生产密钥（阿里云 AK/SK、OSS/SMS、PostgreSQL/Redis/RabbitMQ 口令与内网 IP），`Content Include="**\*"` + `NoDefaultExcludes` 且排除清单未覆盖 `agile/**`、`*.cache` | `Host\NexusStack.WebAPI\agile\config\nexusdtack_api.agileconfig.client.configs.cache:1`；`...\nexusstack_api.agileconfig.client.configs.cache:1`；`NexusStack.Template.csproj:18,22`；`.template.config\template.json:124-134` |
| 严重 | 网关路由管理 API 不可用：全局 `RequestAuthorizeFilter` 对未认证请求直接 401 短路，而网关宿主没有注册 `ApiControllerBase` 要求的 `Authorization-Token` 方案 | `Core\ServiceCollectionExtensions.cs:234,209`；`Core\Filters\RequestAuthorizeFilter.cs:54-60`；`Core\ApiControllerBase.cs:16`；`Gateway\Controllers\ProxyManagementController.cs:8,29-241` |
| 严重 | 网关在 Production/Staging/Test 无法启动：无对应环境配置文件、无 Redis 配置，`UseRedis` 对空连接串抛异常 | `Gateway\appsettings.json:1-19`；`Gateway\appsettings.Development.json:5`；`Core\ServiceCollectionExtensions.cs:295`；`Domain\NexusStack.Redis\ServiceCollectionExtensions.cs:38-41` |
| 高 | 路由持久化在 `AppContext.BaseDirectory` 单文件、非原子写、每副本一份；仓库内 `proxy-config.json` 未被 csproj 引用（死文件），README 文档格式与模型不符 | `Core\Gateway\JsonProxyConfigStore.cs:44,47,177-178,294-308`；`Gateway\NexusStack.Gateway.csproj:12-19`；`Gateway\proxy-config.json:1-4`；`README.md:411-434`；`Core\Gateway\ProxyRouteConfig.cs:9-11` |
| 高 | 动态路由"新增/更新成功"会在配置无效时撒谎：`LoadConfigAsync` 吞掉异常，控制器仍返回 `Ok()`；`TimeSpan.Parse` 与引用完整性无校验 | `Core\Gateway\DynamicProxyConfigProvider.cs:93-96,139-147`；`Gateway\Controllers\ProxyManagementController.cs:64-66,91-93,113-116,239-240` |
| 高 | 网关是裸转发：无认证/限流/关联 ID/重试/熔断/超时/被动健康检查，模型与转换层丢掉了 YARP 的 Transforms/HttpRequest/HttpClientConfig/Passive | `Core\ServiceCollectionExtensions.cs:199-223,318-322`；`Core\Gateway\ProxyRouteConfig.cs:7-16`；`Core\Gateway\ProxyClusterConfig.cs:16-29`；`Core\Gateway\DynamicProxyConfigProvider.cs:102-152` |
| 高 | 两个宿主共用一份全栈注册：反射 DI、目录内全程序集强制加载、`MigrationsAssembly` 硬编码、静态服务定位器、Redis 静态全局、审计过滤器同步依赖 RabbitMQ | `Core\ServiceCollectionExtensions.cs:79-271,280,340-344`；`Infrastructure\ServiceCollectionExtensions.cs:19-37`；`Infrastructure\App.cs:16,34`；`Domain\NexusStack.EFCore\ServiceCollectionExtensions.cs:40`；`Domain\NexusStack.Redis\ServiceCollectionExtensions.cs:42-43`；`Core\Filters\OperationLogActionFilter.cs:85` |
| 高 | `UseHttpLogging` 全字段 + 1MB body + JSON 文本 → 登录口令、Bearer Token 进日志文件 | `Core\ServiceCollectionExtensions.cs:124-130,293`；`Controllers\TokenController.cs:36-40`；`agile\config\nexusdtack_api...cache:1`（日志路径 `/Logs/NexusStackAPI/log.txt`） |
| 高 | 错误响应统一为 HTTP 200（`RequestJsonResult` 不设状态码）；异常过滤器内部二次抛异常；最外层中间件非认证异常不回写响应（空 200） | `Infrastructure\Models\RequestJsonResult.cs:11-16`；`Core\Filters\ApiAsyncExceptionFilter.cs:79,108-117,128-143`；`Core\Filters\RequestAsyncResultFilter.cs:66,78,96,109`（对比 `:53` 的 400）；`Core\Middlewares\ExceptionHandlerMiddleware.cs:22-40` |
| 高 | 生产配置 `ShowStackTrace=True` + `Swagger:Enable=True`：堆栈返回给调用方，接口文档匿名可访问 | `agile\config\nexusdtack_api...cache:1`；`Core\Filters\ApiAsyncExceptionFilter.cs:74-77`；`Core\ServiceCollectionExtensions.cs:284-287`；`Domain\NexusStack.Swagger\ServiceCollectionExtensions.cs:84-116` |
| 中 | CORS 依赖可能不存在的 `Cors` 配置键（缺失→零来源），且 `AllowCredentials` 与通配方法/头并用；转发头 `KnownProxies` 被清空导致 `X-Forwarded-For` 可伪造 | `Core\ServiceCollectionExtensions.cs:157-167,427-435`；`Domain\NexusStack.Serilog\HttpRequestExtensions.cs:19-31`；`Core\Filters\OperationLogActionFilter.cs:80` |
| 中 | 上传目录以 `/static` 匿名暴露且 `ServeUnknownFileTypes=true`、位于鉴权之前；`Storage:Path` 硬编码 `/root/filestorage/uploads`；全仓无上传端点 | `Core\ServiceCollectionExtensions.cs:298,301,419-447`；`appsettings.json:20,22`；`Infrastructure\Utils\FileHelper.cs:10` |
| 中 | 下载接口把整文件转成逗号拼接字节串塞进 JSON，并硬编码 `FileStorageType.Aliyun`（与 `Storage:Type=Local` 矛盾） | `Controllers\DownloadController.cs:59-68`；`appsettings.json:22` |
| 中 | Swagger 内置登录必失败（请求不存在的 `/api/basic/token/password` + 口令加 `swagger` 前缀而服务端要求 base64）；Token 存入无 `Secure/HttpOnly/SameSite` 的 Cookie 并 `console.log` | `Domain\NexusStack.Swagger\Resources\nexusstack-swagger.js:139,148,5-21,53,118-123`；`Core\Services\Users\UserTokenService.cs:107-111`；`Controllers\TokenController.cs:36` |
| 中 | Swagger 的 `SwaggerGen/SwaggerUI` 开关是死配置；`Directory.GetFiles("*.xml")` 全量注入而项目未生成 XML 文档；硬编码 OpenAPI 2.0 | `Domain\NexusStack.Swagger\SwaggerOptions.cs:20,25`；`ServiceCollectionExtensions.cs:62-63,86-87` |
| 中 | Serilog 无代码级 sink 兜底（配置中心无 `Serilog` 节即零日志）；配置引用未引用的 enricher（`WithSpan/WithThreadId/WithThreadName`）导致输出模板字段恒空或绑定失败；Seq 包引用但从未配置；`WorkerEnricher` 每条日志建一个 DI scope | `Domain\NexusStack.Serilog\ServiceCollectionExtensions.cs:16-36`；`NexusStack.Serilog.csproj:9-16`；`WorkerEnricher.cs:19`；`Infrastructure\App.cs:34-36`；`LoggerExtensions.cs:44`；`agile\config\nexusdtack_api...cache:1` |
| 中 | 控制器薄转发/空壳 + 基础设施泄漏（配置回显、Redis key 扫描、控制器里开 EF 事务与执行策略、内存分页、全表加载） | `Controllers\OpenAppConfigController.cs:34-44,58,77`；`Controllers\RedisController.cs:17-21`；`Controllers\UserController.cs:182,232,335`；`Controllers\MenuController.cs:135,149-159`；`Controllers\PermissionController.cs:8-11` |
| 中 | 横切缺失：无入站关联 ID、无 `UseSerilogRequestLogging`、无入站幂等、无缓存头、无 `AddHealthChecks`/`MapHealthChecks`、无优雅停机；`X-TraceId` 条件性写出 | `Core\Filters\RequestAsyncResultFilter.cs:31-34`；`Controllers\HealthController.cs:18-23`；`Domain\NexusStack.RabbitMQ\EventSubscriber.cs:540`（仅消费侧幂等）；全仓 grep `AddRateLimiter|AddHealthChecks|UseSerilogRequestLogging|ShutdownTimeout|AddApiVersioning` 0 命中 |
| 中 | 操作日志列表与导出 50 行重复逻辑、导出无上限整表进内存；改密逐条更新 Token 并逐条删 Redis；缓存失效逐用户循环 | `Controllers\OperationLogController.cs:26-74,84-132`；`Controllers\UserController.cs:384-397`；`Controllers\MenuController.cs:247-252` |
| 中 | Excel 模板函数反射调用任意 `ReportHelper` 方法（无允许列表、模板路径无根约束）；`ToDataTable` O(n²)；列字母 `char` 算术 >26 列即非法；`PDFHelper` 为教程残留且路径必然失败；EPPlus 用非商业许可；约 800 行无调用点 | `Domain\NexusStack.Excel\Export\ExcelExtensions.cs:48-55`；`ExcelHelper.cs:30-42`；`EPPlusExtensions.cs:53-57,192-196,37,177`；`PDFHelper.cs:27,31,37-41,49,51,80-81,117-125`；`ExportStream\ExcelEncryptHelper.cs:24`；`Core\ServiceCollectionExtensions.cs:60`（商业许可被注释） |
| 中 | Dockerfile 为 VS 调试用文件：`COPY publish/` 与 `.dockerignore` 的 `**/publish` 冲突（构建必失败），与发布配置输出目录也不一致；无多阶段/非 root/HEALTHCHECK/VOLUME/compose/CI | `Host\NexusStack.WebAPI\Dockerfile:8-10`；`Host\NexusStack.Gateway\Dockerfile:9-11`；`.dockerignore:22,38`；`Properties\PublishProfiles\FolderProfile.pubxml:11`；`Gateway\NexusStack.Gateway.csproj:9` |
| 低 | 模板残留：`.http` 指向不存在的 `/weatherforecast/` 且变量名仍是 `MineGuard`；`wwwroot/index.html` 空页面；模块 Key 三处命名不一致；两个解决方案文件并存 | `Host\NexusStack.WebAPI\NexusStack.WebAPI.http:1-4`；`Gateway\NexusStack.Gateway.http:3`；`wwwroot\index.html:1-10`；`Program.cs:3` vs `Gateway\Program.cs:4` vs `BackgroundServices\NexusStack.MQService\Program.cs:5`；`.template.config\template.json:15-22` |
| 低 | 路由前缀靠基类/子类 `[Route]` 叠加推断（WebAPI 重复声明、Gateway 叠加一层），而权限键正是路由模板字符串，前缀变化会连带失配 | `Core\ApiControllerBase.cs:16`；`WebAPI\Controllers\BaseController.cs:9`；`Gateway\Controllers\BaseController.cs:9`；`Core\Filters\RequestAuthorizeFilter.cs:120-131` |
| 低 | 校验能力薄弱：无 FluentValidation、无 `[Required]`、仅少量 `[MaxLength]` 且文案与限值不符；业务校验靠控制器手写 `BusinessException`；无 `ModelState` 检查 | `Domain\NexusStack.Core\Dtos\Menus\CreateMenuDto.cs:18`；`Dtos\Users\CreateUserDto.cs:15`；`Controllers\UserController.cs:171-176` |
