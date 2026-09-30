# Spec: NexusStackNext — 真正的微服务 + DDD 后端模板

Status: ready-for-agent
Effort: `nexusstack-next`
参照物：`D:\NexusStack\NexusStackBackend`（只读，未修改一个字节）
证据：`review/01..04-*.md`（4 份，1746 行，逐条带 `file:line`）

> **这份 spec 在 2026-09-30 按 `to-spec` 的模板补过一次形态。** 原文（`## 1. 目标` 起）
> **逐字保留**在文末的附录里——它记录的是当时的推理与实测，不是可以被"整理"掉的草稿。
> 补上来的是模板要求的七节。
>
> 原文的 `Status: ready-for-human`（"等你过目后再开工"）是当时的事实；
> 按模板发布时该字段应为 `ready-for-agent`，所以上面那一行已经改了，历史写在附录里。

---

## Problem Statement

团队要开一个新的 .NET 后端：需要限界上下文，需要**真能跑通**的边界。而上一版的形态让边界
只存在于文档里——四个可部署单元引用同一个 Core，"我是哪个服务"由运行时枚举表达；
28,130 行代码**零测试工程**，CI 是个空目录；实体在构造函数里取静态服务定位器，
于是 `new User()` 在单元测试里直接抛异常——**这个项目从结构上就无法做单元测试**。

同时它还带着几处会安静失效的东西：授权在配置缺失时 **fail-open**（一次配置事故等于全站放开）、
消息不可路由时只记日志却照样 ACK（上游以为发出去了，而没有人收到）、
权限缓存的失效窗口最长 10 小时、刷新令牌明文存库且可重放、数据权限**建模了但零消费点**。

最后一条是关于"证据"的：这些结论都是**读出来的**，而它们的共同点是——
**没有任何检查会在它们复发时说话**。

## Solution

一套**面向领域的 .NET 微服务解决方案模板**：五个平台能力（Identity / Platform / Scheduling /
Auditing / Files）各自是独立的程序集，由**一个**宿主组装（它们是通用子域，一起演进、一起部署），
边缘是一个独立的 YARP 网关；**架构不变量由编译期与测试守着**，而不是靠自觉。

模板本身可被 `dotnet new` 使用，且**不携带任何凭据**——包括那些容易随运行产物一起被打包的东西。

## User Stories

1. As a backend engineer starting a new service, I want the build to fail when two bounded contexts reference each other, so that a boundary violation is caught before review.
2. As a backend engineer, I want a domain layer that depends on nothing, so that I can write `new User()` in a unit test without booting a process.
3. As a backend engineer, I want identifiers supplied by the caller instead of pulled from a static singleton, so that my tests are deterministic.
4. As a backend engineer, I want one aggregate to equal one transaction, so that I never have to reason about partial writes inside a use case.
5. As a backend engineer, I want cross-aggregate and cross-context consistency to be eventual and outbox-driven, so that a failed publish cannot lose the business change.
6. As a backend engineer, I want a message that cannot be routed to fail loudly instead of being acknowledged, so that "sent" and "received" cannot diverge silently.
7. As a backend engineer, I want consumer idempotency keyed by the message identity, so that two legitimate identical operations are not collapsed into one.
8. As a backend engineer, I want each consumer to have its own retry tiers and dead-letter queue, so that one failing consumer cannot force another to reprocess.
9. As a backend engineer, I want a failed handler to release its idempotency slot, so that the retry tiers actually re-invoke it.
10. As a security-minded engineer, I want an unknown authorization decision to deny, so that a configuration accident cannot open the whole surface.
11. As an operator, I want unauthenticated requests to a protected route to fail closed even when the signing key is missing, so that a missing secret degrades to "nobody gets in" rather than "everybody gets in".
12. As an operator, I want the service to refuse to start with a clear message when a required secret is missing, so that I learn it at deploy time instead of at first login.
13. As an operator, I want liveness and readiness to answer different questions, so that a dependency outage stops traffic without restarting the process.
14. As an operator, I want the documentation of every backend aggregated at the edge, so that I can read the whole API surface without exposing the backends.
15. As a platform owner, I want the edge to be the only published entry point, so that edge authentication cannot be bypassed by connecting to a backend directly.
16. As a platform owner, I want configuration priority (environment over config centre over files) to be pinned by a test, so that "I changed it and nothing happened" is impossible.
17. As a template user, I want `dotnet new` to produce a project that builds and passes its own tests, so that the first five minutes are not spent debugging the template.
18. As a template user, I want the generated project to contain no credentials, so that I cannot leak someone else's secrets by using it.
19. As a maintainer, I want every claim about "what is covered" to be machine-checked, so that a statement in the docs cannot quietly become false.
20. As a maintainer, I want each invariant to have been validated by breaking it once, so that "there is a test for this" is evidence rather than a pointer.

## Implementation Decisions

- **Five bounded contexts as separate assemblies, one host, one database with a schema per context.**
  These five are generic/supporting subdomains: they are not where the business differs, and they
  evolve and deploy together. Bounded context, deployable unit, and database are three different
  decisions that were deliberately not forced into a 1:1:1 mapping. Future business contexts get
  their own service and their own database.
- **A shared kernel admitted only by proof**: code moves into it after a second consumer appears,
  not before. The allow-list of what may live there is itself checked.
- **Ports in the application layer, adapters at the host.** The domain layer depends on nothing;
  the application layer does not depend on infrastructure.
- **Every host composes itself explicitly.** There is no module key, no runtime "which service am I"
  enum, and no assembly scanning that decides what a process is made of.
- **Aggregates own an optimistic-concurrency version whose contract is "the version changes if and
  only if observable state changed".** No-op paths deliberately do not bump it.
- **IDs are generated by the application and mapped as never-database-generated**; the database has
  no identity columns.
- **Integration events carry an explicit, versioned name**; routing keys, queue names, and idempotency
  keys derive from it, never from CLR type names.
- **The edge authenticates; contexts authorize.** Authentication is a signature check at the edge;
  per-endpoint authorization happens inside each context.
- **Authorization defaults to deny, including "no requirement declared".** Public endpoints must say so.
- **Configuration priority is environment > config centre > environment-specific file > file**, and it
  is pinned by a test against the host's real configuration manager.
- **Observability**: structured logs always reach the console; traces propagate across the edge to
  backends; readiness checks the dependencies that actually exist for that process.
- **The repository is the template**: what ships is the same tree that is tested, minus local secrets
  and the effort tracker.

## Testing Decisions

- **A good test here crosses a seam the production code also crosses, and asserts external behaviour,
  not implementation detail.** Callers and tests cross the same interface; a test that has to reach
  past it means the module shape is wrong.
- **Test doubles live only at system boundaries** — the database, the broker, the clock, the file
  system — and only one definition of each exists, in a shared test-support project.
- **The invariant checks are the test suite's spine**: they assert the dependency graph, not behaviour,
  and each was validated by breaking the rule once and watching it fail.
- **Modules under test**: the domain aggregates (invariants and version contract), the application
  dispatcher pipeline (validation, transaction boundary, save-on-success only), the message base
  (outbox delivery, retry tiers, dead-lettering, consumer idempotency), the edge (route model,
  routing coverage, fail-closed authentication), and each context's use cases.
- **Prior art in this codebase**: the aggregate version tests (one per aggregate, asserting both
  "changed ⇒ +1" and "no-op ⇒ unchanged"), the inbox contract test that every implementation
  inherits, and the structural checks that fail loudly when they have nothing to scan.
- **Real dependencies are used where a double would prove nothing**: PostgreSQL for persistence
  conventions and concurrency, a real broker for publish confirmation, retry routing, and dead-lettering.
  Where those are unavailable the tests skip **with the reason in the skip message** — a skip must be
  distinguishable from a pass.
- **Suite hygiene is a test concern too**: the suite runs projects serially against a shared database,
  prints the target host and per-project durations, and holds a global lock — because the last time it
  did not, it took down the shared database and the configuration centre with it.

## Out of Scope

- No front end; the back end only.
- No data migration from the previous system.
- No database provider other than PostgreSQL.
- No container runtime dependency for local development or tests (there is none on the target machine);
  orchestration is process-level and middleware stays external.
- No business functionality for the four supporting contexts beyond a working end-to-end slice each —
  this effort delivers the skeleton plus one complete vertical slice per context.

## Further Notes

- The original plan — including the reference-repository findings that motivated each decision, the
  keep/cut lists, and the open questions that were still unanswered at the time — is preserved verbatim
  in the appendix below. **It is the evidence, not the summary.**
- Decisions are recorded as ADRs: system-wide ones at `docs/adr/`, context-scoped ones under each
  context. The tracker lives under `.scratch/`, and its conventions are described in `docs/agents/`.
- Where this spec and a later ADR disagree, **the ADR is right** — this document records what was
  decided at the time, and ADRs record why it changed.

---

# 附录：原始方案（历史，逐字保留）

> 下面这一部分**没有被改写**。它的 `Status: ready-for-human`、它对票据与轮次的引用、
> 以及当时未答的开放问题，都是那一刻的真实状态。改写的部分只在上面的七节里。

## 1. 目标

1. **边界由编译期保证**：5 个限界上下文各自是独立程序集，跨上下文误用**编译不过**，而不是靠自觉。

   > **2026-09-29 修订**：这五个是**平台能力**（通用/支撑子域），已合并为**一个宿主与一个库**
   > （库内按 schema 分开）。**上面那句"各自独立部署"不再是本项目的形态。**
   > 但"边界由编译期保证"这条**没有变**——程序集边界照旧。
   > 见 [ADR-0013](../../docs/adr/0013-platform-capabilities-are-one-host.md)。
2. **DDD 不是目录表演**：领域模型里真的有聚合、值对象、强类型 ID、领域事件与不变量，且**不变量在领域对象内部强制**。
3. **保留已验证的资产**：把原项目真正做对的东西原样移植（见 §6），不做"推倒重来式"的浪费。
4. **补齐微服务缺口**：Outbox/Inbox、版本化事件契约、可观测性、正确的 HTTP 语义、架构不变量测试。
5. **仍是模板**：仓库能 `dotnet new` 出可用工程，且**打包时不带任何凭据**。
6. **规范内置**：MattSkills 规范已就位（`AGENTS.md` / `docs/agents/` / `CONTEXT-MAP.md` / `docs/adr/` / `.scratch/`）。

## 2. 非目标

- 不做前端（NexusStackPro 不在本次范围）。
- 不做旧库到新库的数据迁移，除非后续单独立项。
- 第一轮**不实现全部 5 个上下文的业务功能**，只保证骨架成立 + Identity 端到端跑通（见 §9）。
- 不支持 PostgreSQL 以外的数据库（README 的"三库支持"本就是空头承诺，见 §7）。
- 不引入容器运行时依赖（本机无 Docker/Podman/WSL，见 ADR-0005）。

---

## 3. 为什么要重做（证据，不是感觉）

### 3.1 根因只有一条

> 四个可部署单元（WebAPI / Gateway / MQService / PlanTaskService）**全部** `ProjectReference` 同一个 `NexusStack.Core`，
> 而 `NexusStack.Core` 又拖进全部 7 个技术库。"我是哪个服务"由运行时枚举 `CoreServiceType`（`Infrastructure/Enums/PlatformType.cs:81-99`）表达。
> —— `review/01` 概览

后果：网关被迫携带 EF Core、PostgreSQL、Aliyun OSS、SkiaSharp、FFmpeg、Excel；
跨服务误用能编译通过；任何边界讨论都只能停留在口头。**下面所有问题都是这一条的症状。**

### 3.2 DDD 的现状是零

全仓 `AggregateRoot` / `ValueObject` / `IDomainEvent` / `BoundedContext` **零命中**。
23 个实体全是无行为 POCO，本该属于它们的不变量散在 Service 里：
`Role.IsSystem` 在 `RoleService.cs:32-35`，`Menu.IdSequences` 的物化路径在 `MenuService.cs:38-62` 用字符串拼接维护。
`IRepository` 只存在于技术库 EFCore 里，是 64 个成员的浅穿透接口（其中 10 个成员零调用）。

### 3.3 三个阻断级缺陷（重做时必须先解决，否则新代码会复发）

| # | 缺陷 | 位置 | 后果 |
|---|---|---|---|
| 1 | 实体无法在进程外构造：构造函数里调 `SnowFlake.Instance`，依赖 `App.Init()` 之后的静态 `IServiceProvider` | `Infrastructure/App.cs:29-32`、`ServiceCollectionExtensions.cs:280`、`AuditedEntity.cs:10-13` | 任何单测里 `new User()` 直接抛异常 → **这个项目从结构上就无法做单元测试** |
| 2 | 主键双权威：`Entity.cs:15` 赋雪花 ID，而 22 张表全是 `GENERATED BY DEFAULT AS IDENTITY`，无一处 `ValueGeneratedNever` | `Entity.cs:15` vs `SqlMigration/InitDatabase.sql:9` | 序列停在 1，`Id=0` 路径插入拿到 1，与种子 admin（`Id=1`）相撞 |
| 3 | 事件路由键 = CLR 类型全名，且事件注册表靠扫描**消费端** Handler 发现 | `EventSubscriber.cs:196/255/294-297` | **按上下文拆程序集的那一刻，消息链静默断掉**——没有编译错误，没有启动失败 |

第 3 条对本项目是致命的：我们要做的第一件事就是拆程序集。

### 3.4 其余高价值结论（详见各报告）

- **消息**：不可路由消息只记日志却照样 ACK（`EventPublisher.cs:36-41/505-510`）；重试队列 DLX 按事件名回主交换机，导致一个 Handler 失败让**同事件所有** Handler 重收（`EventSubscriber.cs:255` vs `:196/:294-297`）；无 Outbox，发布失败留下永久 Pending 的孤儿任务（`AsyncTaskService.cs:47-54`）。
- **后台服务**：`PlanTaskService` 是**事实空壳**——写调度缓存的种子服务注册被注释（`Core/ServiceCollectionExtensions.cs:262`），`CronScheduleService.cs:101-107` 因此永远静默跳过，还以 1Hz 空转。
- **宿主**：`RequestJsonResult` 不设状态码，**异常与 401 全是 HTTP 200**；异常中间件对非鉴权异常写日志但不写响应体；日志 `LoggingFields.All` + 1MB body 会把口令与 Token 写进日志。
- **数据**：`EnableRetryOnFailure()` 与裸 `BeginTransactionAsync()` 并用，唯一的事务调用方 `PermissionService.cs:98` **必抛**；无 UoW（写方法各自 `SaveChanges`）；`ServiceBase.cs:40/56` 的 `ChangeTracker.Clear()` 会静默丢弃同请求内其它服务的改动；批量/软删走商业库 `Z.EntityFramework.Extensions` 并**绕过审计拦截器**。
- **鉴权**：登录无验证码校验（`ValidateCaptchaAsync` 零调用）、无锁定、无速率限制，且泄露用户是否存在；refresh token 明文存储且可重放。
- **数据权限**：`Permission.DataRange` 与 `ICurrentUser.RegionIds` **建模了但零消费点**——数据权限实际上不生效。
- **网关**：路由管理 API 因鉴权方案未注册而必然 401（HTTP 体里是 200）；无认证/限流/关联 ID/重试/熔断；生产配置缺失导致网关起不来。

### 3.5 两个必须纠正的"评审结论"

- `review/03` 称"模板会分发生产密钥"。核实：`git ls-files -- agile/` **为空**——这些文件**未被 git 跟踪、没有推到 GitHub**。
  真实风险是打包：`NexusStack.Template.csproj:18,22` 的 `Content Include="**\*"` + `NoDefaultExcludes=true` 未排除 `*.cache`，
  从**这台机器** `dotnet new install .` 会把凭据打进模板包。→ 处理方式见 §9 阶段 1 的第 1 张票（加排除规则，文件保留）。
- `review/02` 称"多数据库支持不存在"：属实，且 `template.json:61-72` 的 `DatabaseProvider` **只有一个 choice**。
  也就是说 README 的卖点与模板实际能力不一致——本项目的 ADR-0002（单库 PG）是把现状**写成事实**，不是能力倒退。

---

## 4. 架构

### 4.1 解决方案结构

```
NexusStackNext.slnx
├── Directory.Build.props            统一 TFM / Nullable / TreatWarningsAsErrors / 分析器
├── Directory.Packages.props         中央包版本管理
├── src/
│   ├── BuildingBlocks/              被 ≥2 个上下文证明需要才允许存在（不变量 7）
│   │   ├── BuildingBlocks.Domain            实体/聚合根/值对象/强类型ID/领域事件/Result
│   │   ├── BuildingBlocks.Application       命令与查询分发、验证、事务边界、集成事件契约基类
│   │   ├── BuildingBlocks.Infrastructure    EF Core 基座、Outbox 投递器、总线适配、IClock、ID 生成
│   │   └── BuildingBlocks.Contracts         跨上下文契约（EventName 版本化，ADR-0007）
│   ├── Services/
│   │   ├── Identity/                Domain / Application / Infrastructure / Api / Contracts + CONTEXT.md
│   │   ├── Platform/                ┐
│   │   ├── Scheduling/              ├ 第一轮只建骨架（Domain + Api 占位 + CONTEXT.md）
│   │   ├── Auditing/                │
│   │   └── Files/                   ┘
│   └── Gateway/                     YARP 边缘（ADR-0003）
├── tests/
│   ├── BuildingBlocks.*.Tests
│   ├── Identity.Domain.Tests        领域不变量，纯内存
│   ├── Identity.Application.Tests   命令/查询，内存仓储
│   ├── Identity.IntegrationTests    真实测试库，每测试独立 schema
│   └── Architecture.Tests           ★ 把 AGENTS.md 的 8 条不变量变成断言
├── aspire/
│   ├── AppHost                     编排 5 服务 + 网关（中间件走外部，ADR-0005）
│   └── ServiceDefaults             OTel / 健康检查 / 韧性
└── deploy/                         Dockerfile（每服务一份）+ compose（生产）
```

**`Architecture.Tests` 是本方案的关键设计**：上一版把"边界"寄托在人的自觉上，这一版用 NetArchTest 把
`AGENTS.md` 的 8 条不变量写成断言，**违反即测试失败**。这是"编译器帮你守边界"落地的最后一环。

### 4.2 上下文、数据归属与关系

| 上下文 | 数据库 | 拥有 |
|---|---|---|
| Identity | `nexus_identity` | 用户、角色、权限、菜单/资源、令牌 |
| Platform | `nexus_platform` | 全局设置、开放应用配置、区域字典 |
| Scheduling | `nexus_scheduling` | 异步任务、计划任务、执行记录 |
| Auditing | `nexus_auditing` | 操作日志、令牌流转、登录审计 |
| Files | `nexus_files` | 文件元数据、存储位置、下载授权 |

关系与 false friends 见 `CONTEXT-MAP.md`（已写）。要点：

- **认证在网关，授权在上下文**（ADR-0003）。网关只验签，不做"这个用户能不能做这件事"。
- **所有上下文 → Auditing**：只写上下文，无人反向同步调用它。
- **Scheduling 不调用别人的业务接口**，只发 `TaskDue` 类事件——否则它每加一个上下文都要改，会变成上帝服务。

### 4.3 架构不变量

已写入 `AGENTS.md`，共 8 条。其中对本次重做最关键的：

- 不变量 3：`*.Domain` 不依赖任何基础设施 —— 直接根治 §3.3 缺陷 1（实体里不再有静态 ID 生成）。
- 不变量 2：上下文只通过 `*.Contracts` 通信 —— 配合 ADR-0007 根治缺陷 3。
- 不变量 8：每个宿主显式组装 —— 消灭 `InitApplication(moduleKey)` 式隐藏装配与 `CoreServiceType` 枚举。

---

## 5. 横切关注点设计

### 5.1 认证与授权

- **令牌**：access + refresh。refresh **只存哈希**、**一次性使用**、每次刷新轮换（修正原项目的明文与重放缺陷）。
- **授权**：保留原项目最值得称赞的设计——登录/角色变更时把用户权限**预计算**成 `HashSet<"routetemplate:METHOD">`，
  鉴权时 O(1) 查（`UserContextCacheService.cs:96-119`）。
- **但修三处**：缓存加 single-flight；失效改版本号而非裸 `DEL`；TTL 从 10h 降到合理值并配事件驱动失效。
- **登录**：补验证码校验（原项目写了方法却零调用）、失败计数锁定、错误信息不区分"用户不存在"与"密码错误"。

### 5.2 消息、Outbox / Inbox

- 事件契约名**显式版本化**（ADR-0007），拓扑集中声明。
- **Outbox**：领域事件与聚合写入同一事务；投递器负责发到 RabbitMQ，成功才标记。
- **Inbox**：消费端幂等表，按 `EventName + MessageId` 去重。
- **保留**原项目的幂等三段式（先占键 / 失败删键 / 成功留键）与**每档位独立延迟队列**。
- **补齐**：DLQ 消费者与指标；`BasicReturn` 必须导致**失败**而不是 ACK；重试回程路由按**队列**而非事件名。

### 5.3 缓存

只缓存**读模型**，不缓存领域实体。所有失效走版本号。鉴权热路径上的缓存必须有 single-flight。

### 5.4 配置

`appsettings.*.json` 与 AgileConfig 的**优先级一次定清楚并写进 README**——否则"改了不生效"会成为长期困惑源。
密钥一律走环境变量/配置中心，**不进仓库**。

### 5.5 可观测性

Aspire `ServiceDefaults` 提供 OpenTelemetry；Serilog 保留（原项目已验证）；
**关联 ID 从网关生成并贯穿所有服务**（原项目完全没有）。

### 5.6 API 契约与错误处理

修正原项目"一切 HTTP 200"的问题：正确的状态码 + `ProblemDetails`。
分页统一为**游标或 offset 两种明确约定之一**（原项目控制器里直接跨表 join 并同步分页，`OperationLogController.cs:57,74`）。

### 5.7 ID 与时间

- ID：应用侧生成，唯一权威（ADR-0009）；WorkerId 由配置中心统一下发，不各服务本地拿。
- 时间：`IClock` 显式注入——原项目 `CronScheduleService` 用的静态时间导致无法测试。

---

## 6. 保留清单（移植清单，不是重写清单）

| # | 资产 | 证据位置 | 移植时的修正 |
|---|---|---|---|
| 1 | RBAC 预计算 + O(1) 鉴权 | `UserContextCacheService.cs:96-119` | 补 single-flight、版本号失效、TTL |
| 2 | 幂等三段式协议 `ACQUIRED\|/DUPLICATE\|` + 分档延迟队列 | `EventPublisher.cs:112-126`、`EventBus/DelayTier.cs` | 幂等键不要绑业务标识（会吞合法重发） |
| 3 | Ardalis.Specification 查询对象 | `NexusStack.EFCore` | 保留读路径用法，不做"唯一仓储" |
| 4 | Mapster `ProjectToType` 投影读路径 | 迁移已核实完成，无 AutoMapper 残留 | 直接沿用 |
| 5 | 软删除全局过滤器（`ReplacingExpressionVisitor`） | `NexusStack.EFCore` | 保留 |
| 6 | `SaveChangesInterceptor` 审计位点 | `NexusStack.EFCore` | **堵住**批量/裸 SQL 绕过（ADR-0008） |
| 7 | `IEntity` / `ISoftDelete` / `IAuditedEntity` 契约分层 | `NexusStack.EFCore` | 保留 |
| 8 | 客户端生成 ID 的方向 | `Entity.cs:15` | 去静态化 + 唯一权威（ADR-0009） |
| 9 | `JsonProxyConfigStore` 的并发处理 | 网关 | 换持久化位置（原为 `AppContext.BaseDirectory`，每副本一份、非原子写） |
| 10 | `PlatformType` `[Flags]` 多端建模 | `PlatformType.cs:10-37` | **修 `All = 0` 语义冲突**（一处当"全部"、一处当"无"，见 `PermissionService.cs:48/94`） |
| 11 | Cron 基类形状 | `Schedules/` | 加可注入时钟、锁续租（原 60s 不续租） |
| 12 | Hub 无客户端可调用方法 + 专用 SignalR 认证 Scheme | SignalR 设计 | 加 Redis backplane |
| 13 | 动态路由能力 | 网关 | 加鉴权 + 真正生效（原 `ConvertConfig` 丢弃 Transforms 等字段） |

## 7. 明确砍掉

| 砍掉 | 理由 |
|---|---|
| 多数据库 provider 支持 | README 的空头承诺；`template.json:61-72` 只有一个 choice；与"每服务一库"冲突 |
| `Z.EntityFramework.Extensions` | 商业授权 + 绕过审计（ADR-0008） |
| 静态服务定位器 `App` / `RedisHelper` | 不变量 5；也正是单测做不了的原因 |
| `CoreServiceType` 运行时枚举 | 不变量 8；"我是谁"应由程序集边界回答 |
| 反射式大爆炸 DI 注册 | 边界不可见，且任何程序集都能被扫进来 |
| Excel 模板反射调用任意 `ReportHelper` 方法 | 安全隐患 |
| `agile/config/*.cache` 明文凭据随模板分发 | §3.5 |
| `ServiceBase` 里的 `ChangeTracker.Clear()` | 静默丢弃同请求内其它改动 |

## 8. 测试策略

**约束**：无容器运行时 → Testcontainers 不可用（ADR-0005）。因此分四层：

1. **领域单测**：聚合不变量，纯内存、无 IO。这是重做后**第一次**能写的一层（原项目 `new User()` 就抛）。
2. **应用层测试**：命令/查询 Handler + 内存仓储。
3. **架构测试**：NetArchTest 断言 8 条不变量（`Architecture.Tests`）。
4. **集成测试**：针对一个**真实可用的测试 PostgreSQL**，每个测试用独立 schema + 事务回滚。
   需你提供测试库连接信息（或允许复用现有 PG 上的一个专门 database）。

## 9. 交付分期

### 阶段 1（本轮，目标：骨架 + Identity 端到端）

票据见 `issues/`。顺序与依赖已在票据的 `Blocked by` 中声明。概览：

- 解决方案骨架、`Directory.Build.props`、中央包管理
- `BuildingBlocks.Domain` / `Application` / `Infrastructure` / `Contracts`
- Identity 的 Domain → Application → Infrastructure → Api 端到端
- 认证（令牌签发/轮换）与授权（预计算 + 过滤器）
- Gateway（YARP + 路由持久化 + 限流 + 关联 ID + 管理 API 鉴权）
- 其余 4 个上下文骨架 + `CONTEXT.md`
- Aspire AppHost（外部中间件接线）
- 四层测试落地
- 模板打包与凭据排除
- CI（原 `.github/workflows` 是空的）

### 阶段 2（后续）：Platform / Scheduling / Auditing / Files 的功能实现
### 阶段 3（后续）：模板参数面打磨、文档、发布

## 10. 开放问题（需要你定，或我先给建议）

1. **数据权限**（`DataRange` + `RegionIds`）：原项目建模了但零消费点。补齐还是明确砍掉？
2. **认证形态**：自研 JWT（原方案）还是 OpenIddict / Authentik？git 里有 `dev-authentik` 分支，说明你考虑过。
3. **审计写入路径**：各上下文 Outbox 发事件 → Auditing 消费（本方案默认），还是各上下文同步写本地审计表？
4. **OSS / 短信 / FFmpeg / Excel 导出**：这些能力归 Files，还是拆出独立的集成层？
5. **模板参数面**：`dotnet new` 暴露哪些开关（选哪些上下文、是否含网关）？
6. **测试库**：能否提供一个可用的 PostgreSQL（远端即可）用于集成测试？
