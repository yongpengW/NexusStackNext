# NexusStackNext

面向领域的 .NET 微服务解决方案模板。五个平台能力由一个宿主组装，边界由**编译期与测试**保证，
而不是靠自觉。

> 与上一版（`NexusStack`）的区别不是"用了更多技术"，而是**把上一版踩过的坑变成了会失败的检查**。
> 每一条架构不变量都对应一个真实发生过的缺陷，见 `docs/adr/` 与 `AGENTS.md`。

## 它长什么样

**每个限界上下文都是同一个四层结构**，五个上下文各一套：

```
src/
  BuildingBlocks/          共享内核：被两个以上上下文证明需要的东西
    Domain/                实体、聚合根、值对象、强类型 ID、领域事件、Result、权限键
    Application/           命令/查询分发、校验管线、事务边界、集成事件契约、授权判定
    Infrastructure/        消息基座（Outbox/Inbox/拓扑/消费策略）、ID 生成
  Services/<Context>/      每个上下文四层，五个上下文完全一致：
    <Context>.Domain/            聚合、不变量、领域事件、值对象
    <Context>.Application/       端口（I*Store / I*Provider）+ 用例服务
    <Context>.Infrastructure/    适配器（内存 / 本地磁盘）
    <Context>.Endpoints/       模块：端点（Add*Module / Map*Endpoints），**不是宿主**
  Gateway/                 边缘：路由模型 + YARP 宿主
tests/
  <Context>.Application.Tests/   每个上下文一个
  BuildingBlocks.*.Tests/        领域基元、应用基座、消息基座
  Contexts.Tests/                各上下文的领域层测试（Identity / Platform / Scheduling / Auditing / Files）
  Architecture.Tests/            八条不变量变成断言
```

五个上下文各有一条**端到端的真实链路**，且都不依赖外部中间件：

| 上下文 | 链路 |
|---|---|
| Identity | 用户 → 角色 → 菜单 → 端点 → 权限键 → 授权判定 |
| Platform | 写入配置 → 存储 → 经边缘读出 |
| Auditing | 事件进入 → 幂等去重 → 落库（**只写，没有查询端点**） |
| Files | 上传 → 字节真的落盘 → 下载 → 删除 |
| Scheduling | 定义任务 → 调度节拍触发 → 下次时刻推进 |

每个上下文自带 `CONTEXT.md`（词表，含 `_Avoid_` 反义词）与 `docs/adr/`（上下文级决策）。

## 八条架构不变量

它们是这个模板的核心，**违反即构建失败**（`tests/Architecture.Tests`）：

1. 一个限界上下文独占自己的数据，不做跨上下文 join。
2. 上下文之间只通过 `*.Contracts` 通信。
3. `*.Domain` 不依赖任何基础设施。
4. 一个聚合 = 一个事务；跨聚合与跨上下文的一致性经 Outbox 最终一致。
5. 禁止服务定位器（不得有静态 `IServiceProvider`）。
6. ID 由调用方提供，不在构造函数里取全局状态。
7. 共享代码要被**第二个消费者**证明需要，才允许上移 `BuildingBlocks`。
8. 每个宿主显式组装自己，不用 `InitApplication(moduleKey)` 式的隐藏装配。

完整说明见 `AGENTS.md`。

## 要求

- **.NET SDK 10.0**（`global.json` 已固定）。
- **一个 JWT 签名密钥**（`Jwt:SigningKey`，≥32 字节）——见下。
- **一个根账号**（`Identity:Root:UserName` / `Identity:Root:Password`）——**推荐**，见下。

**跑起来不需要任何外部中间件。** 平台宿主加边缘（两个进程）可以在一台干净的机器上全部启动并端到端跑通：
宿主当前装配的是内存与本地磁盘适配器，消息基座也有内存实现。

> **但平台宿主需要一个签名密钥才起得来。** 没配 `Jwt:SigningKey` 时它是**启动即失败**：
> `OptionsValidationException: Jwt:SigningKey 至少需要 32 字节`，进程退出、健康检查无从应答。
> 这是**有意**的（认证能力的配置不该缺省 succeed），但"干净机器能跑"这句话必须把它说清楚：
>
> ```powershell
> $env:Jwt__SigningKey = "至少32字节的签名密钥，别提交进仓库"
> ```
>
> 网关那边**不对称**：缺密钥时它照常启动，而**所有要求认证的路由一律 401**（fail-closed，
> 并且在 stderr 说明缺什么）——编排系统不会因此重启风暴，运维也看得见。
> 两侧的取舍不同，是因为"边缘不可用"与"后端不可用"的后果不同。
> `scripts/run-host.ps1 -Init` 生成的骨架里带这一项。

> **另外：权限链的第一环需要一个根账号。** 没配 `Identity:Root:*` 时宿主照常启动（只是跳过播种），
> 但**没有任何人能授权**：菜单建不出来 ⇒ api-resource 挂不上 ⇒ 角色授不到权限 ⇒
> 所有受权限保护的端点一律 403（票据 67 的现象）。
>
> ```powershell
> $env:Identity__Root__UserName = "root"
> $env:Identity__Root__Password = "改成你自己的口令"
> ```
>
> 播种是**幂等**的：存在同名账号就跳过，**绝不用配置里的口令覆盖它**。
> 它走 `IsRoot` 旁路、不做权限判定——所以**上线后第一件事是轮换口令**，
> 不需要它的环境里干脆别配。全部取舍写在 `env/README.md` 那一节。

### 让向导带你走一遍（推荐）

上面那些值散在配置中心后台、broker 与测试库里，抄起来烦、重讲一遍更烦。
`scripts/setup-wizard.sh` 是一个**交互式向导**：它按阶段打开该开的页面、说清点哪里、
把你复制的值写进 `env/*.dev`（幂等，重跑时以已有值作默认），并在每一步告诉你还剩几阶段。

```bash
bash scripts/setup-wizard.sh          # 需要 bash（Windows 上 git bash 即可）
```

它只做**只有人能做的事**——去后台点眼睛图标、去 broker 那台机器抄口令。
能由 agent 做的（生成签名密钥、写文件、验证）它自己做；CI 一个 secret 都不需要，所以它不设任何 secret。

生产适配器的状态（**这是实测过的状态，不是"计划"**）：

| 能力 | 端口 | 实现状态 |
|---|---|---|
| 持久化（基座） | `IOutboxStore` / `IInboxStore` | ✅ EF Core + **PostgreSQL** 已实现（`identity` schema，见 `docs/adr/0002-postgres-per-context.md`） |
| 持久化（各上下文） | 各 `I*Repository` | ⚠️ **Identity** 有 EF 实现；Platform / Scheduling / Auditing / Files **只有内存适配器** |
| 消息 | `IEventBus` | ✅ RabbitMQ 已实现（发布确认 + `mandatory`，6 条真 broker 验收） |
| 配置中心 | —— | ✅ AgileConfig（**读**；写入需要管理 API 凭据，未接） |
| 缓存 | —— | ❌ Redis **尚未接入**（权限缓存是进程内的，见票据 08 的说明） |

> **发件箱要能真的发出去，宿主必须调 `AddNexusStackRabbitMqEventBus(...)`**——
> 它一次做两件事：接上总线、启用投递循环。平台宿主在配了 `RabbitMQ` 节时会调用它。
> 没配总线时**没有投递循环**，事件留在发件箱里不再前进——
> 那是一件看得见的事，比"接一个每次发布都失败的总线"好（后者会把每条消息记成失败尝试）。
>
> 这条曾经**真的发生过**：投递器、退避策略、八档退避表都写完了，而没有任何东西调用它们。
> 第 16 轮 review 发现并修好了。
>
> **这类"写完了但没有任何东西调用它"的缺口，只有从"文档声称的"与"代码实际做的"之间的差去看才找得到。**

> 连接信息一律走环境变量或配置中心，**不入库**。`scripts/assert-no-credentials.ps1`
> 会在 CI 里断言模板生成物不含任何凭据。

## 命令

```powershell
dotnet build NexusStackNext.slnx     # 全量构建（警告即错误）
dotnet test  NexusStackNext.slnx     # 全部测试，含架构不变量
```

跑起来（各占一个终端）：

| 端点 | 平台宿主（5191） | 网关（5190） |
|---|---|---|
| `/health`、`/health/live` | 进程还能应答（**不查依赖**） | 同左 |
| `/health/ready` | 依赖可用（**查**） | 每个 cluster 至少一个目标就绪 |
| `/openapi/v1.json` | 它自己的文档 | **聚合文档**：网关自己 + 每个后端 |
| `/swagger` | API 参考界面（**仅 Development**） | API 参考界面（**总是开**） |

**文档只在边缘对生产开放。** 业务服务上的界面会绕过边缘暴露全部 API 面，
所以那里仅 Development；而网关就在边缘上，生产也要能看——那正是聚合的意义。

网关还有 `/gateway/openapi/sources`，报告每个来源是否取到、各多少条路径。
后端挂掉时聚合文档**仍然可服务**（少了那个来源的接口），而 `complete` 会变成 `false`——
**"文档少了几条"必须看得见**，否则它和"接口本来就不存在"分不开。

网关会通过下游 `/health/ready` 摘除故障目标并自动恢复；所有目标已被摘除时返回 503。
默认限流按调用方与路由隔离，429 附带 `Retry-After`。路由管理的并发写入在进程内串行处理，
通过校验并保存后热更新；查询返回进程接受的配置，直接编辑文件需重启。
配置参数、异步应用语义和单实例边界见 [网关决定](docs/adr/0003-yarp-edge.md#故障处理与配置发布2026-10-01)。

```powershell
dotnet run --project src/Hosts/NexusStackNext.PlatformHost          --urls http://127.0.0.1:5191
dotnet run --project src/Gateway/NexusStackNext.Gateway               --urls http://127.0.0.1:5190
```

**在 Visual Studio 里跑**：需要**两个进程**——平台宿主是后端，网关是边缘。
只启动网关时 `/health/ready` 会报 503；业务请求仍会先受认证保护，允许转发的请求在目标被摘除后返回 503，探测收敛前可能是 502。

`launchSettings.json` 不能声明"哪些项目一起启动"（那是 VS 的解决方案级设置，存在 `.vs/` 里，不进仓库），
所以需要手动设一次：

> 右键**解决方案** → 属性 → 通用属性 → 启动项目 → **多个启动项目** →
> `NexusStackNext.PlatformHost` 与 `NexusStackNext.Gateway` 都设为「启动」

F5 之后浏览器会停在 `/swagger`——那是**网关上的聚合文档**，含两个宿主与五个上下文的全部接口。

边缘的路由表可以查询，不必去读配置文件：

```powershell
Invoke-RestMethod http://127.0.0.1:5190/gateway/routes
```

两条规范检查（CI 里作为失败条件）：

```powershell
pwsh ./scripts/check-tracker.ps1           # 跟踪器与规范的一致性
pwsh ./scripts/assert-no-credentials.ps1   # 模板生成物不含凭据
```

### 边缘上有什么

| 路由 | 路径 | 认证 |
|---|---|---|
| `platform-read` | `/api/platform/{**catch-all}` | 公开（**只放 GET**） |
| `platform-write` | `/api/platform/{**catch-all}` | **要求认证**（只放 PUT/DELETE） |
| `identity-info` | `/api/identity` | 公开 |
| `identity-login` | `/api/identity/login` | **公开**（精确路径） |
| `identity-refresh` | `/api/identity/refresh` | **公开**（精确路径） |
| `identity-self-register` | `/api/identity/users` | **公开**（精确路径） |
| `identity-management` | `/api/identity/{**catch-all}` | 要求认证 |
| `scheduling-info` | `/api/scheduling` | 公开 |
| `scheduling-management` | `/api/scheduling/{**catch-all}` | 要求认证 |
| `files-api` | `/api/files/{**catch-all}` | 要求认证 |

> **中间三条精确路径不是冗余，是修出来的。** `identity-management` 是一条**前缀**路由，
> 它会把 `/api/identity/login`、`/refresh`、`/users` 一起吞进去——于是**登录本身需要一个令牌**，
> 而所有测试仍然是绿的（集成测试直打宿主，跳过了网关的路由策略）。
> YARP 让更具体的路由胜出，所以这三条精确路由把它救了回来。
> 守这件事的有两条防线：静态读 `routes.json` 的 `AnonymousEndpointsAreReachableTests`，
> 与手动跑的 `scripts/verify-user-journey.ps1`（两个真进程 + curl）。

**Auditing 没有路由，这是刻意的**：它是只写上下文，事件从消息总线进入。
给它开一条边缘路由等于让任何人都能注入审计记录——而审计的全部价值就在于它不可伪造。
**"是一个服务"与"在边缘上可达"是两件事。**

> 认证形态已由 **ADR-0014** 定下：令牌由 Identity 签发、网关验签、各上下文授权。\n> 要求认证的路由现在真的会验签（HS256）；**没配签名密钥时**一律返回 401 并在 stderr 说明缺什么。
> 定下形态后替换宿主的默认认证方案即可，路由表与策略名都不用改。

## 配置

优先级**从高到低**（ADR-0006）：

```
环境变量  >  AgileConfig  >  appsettings.{Environment}.json  >  appsettings.json
```

环境变量最高，是为了让容器编排能在不改镜像的前提下覆盖任何一项。
这条顺序**有测试守着**（`tests/Composition.Tests/ConfigurationPriorityTests.cs`）——
因为它写错了不会报错，只会静默取到错的值。

**AgileConfig 是可选的外部依赖。** 没配置 `AgileConfig:AppId` / `Nodes` 时应用**照常启动**，
只记一条日志说明走了降级路径；配置中心**不可达**时同样不致命。
配置中心是运行时的便利，不该变成启动时的单点——否则新克隆的仓库第一印象就是"跑不起来"。

连接信息一律走环境变量，**不进仓库**（双下划线是 .NET 的层级分隔约定）：

```powershell
$env:AgileConfig__AppId  = "your-app-id"
$env:AgileConfig__Secret = "your-secret"
$env:AgileConfig__Nodes  = "http://your-agileconfig:5000"
```

**配置中心的应用划分**见 [`config/README.md`](config/README.md) 与
[[ADR-0012](docs/adr/0012-agileconfig-app-layout.md)：一个基座应用 + 一个平台宿主应用，
宿主关联基座。`config/*.template.json` 是可**直接导入 AgileConfig** 的模板，不含任何凭据。

两条不要越过的线：**基座里不放连接串**（每个上下文独占一个库，ADR-0002）、
**基座里不放 `WorkerId`**（雪花 WorkerId 必须每进程唯一，共享会产生重复 ID）。

日志的配置形态是标准的 `ReadFrom.Configuration`——`Using` / `WriteTo` / `LevelSwitches` /
`MinimumLevel:ControlledBy` 都吃，从 AgileConfig 导出的配置可以**原样使用**。
日志用 Serilog：**控制台永远输出**——"一行日志都没有"是排障时最糟的处境，
参照仓库的 `UseSerilog` 收到空配置就等于没有 sink，而这恰恰发生在最需要日志的时候。
Seq 只在配置了 `Serilog:Seq:ServerUrl` 时接入，没有 Seq 也能正常启动。
## 用它创建新项目

```powershell
dotnet new install .
dotnet new nexusstack-next -n MyProject
```

生成物自带全部规范文件（`AGENTS.md`、`CONTEXT-MAP.md`、`docs/adr/`、`docs/agents/`、每个上下文的 `CONTEXT.md`），
即"规范随模板走"。

## 怎么在这个仓库里工作

本仓库使用 MattSkills 约定：

- **跟踪器**：`.scratch/<feature-slug>/` —— `spec.md`、`issues/NN-*.md`、`map.md`。
  约定见 `docs/agents/issue-tracker.md`。
- **词表**：`CONTEXT-MAP.md` 指向每个上下文的 `CONTEXT.md`。写代码时用词表里的词，
  别用 `_Avoid_` 里列的同义词。
- **决策**：`docs/adr/` 记系统级，`src/Services/<Context>/docs/adr/` 记上下文级。
- **架构不变量**：改代码前先看 `AGENTS.md`；改完跑 `dotnet test`。

## 给 AI agent 的话

`AGENTS.md` 是本仓库对 agent 的指令，`docs/agents/` 是具体约定。
`NexusStackNext.slnx` 是解决方案文件，`Directory.Build.props` 是构建基线
（警告即错误、可空性、XML 文档）。

