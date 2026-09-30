# AGENTS.md

## Agent skills

### Issue tracker

Issues, specs, and tickets live as local markdown files under `.scratch/<feature-slug>/` in this repo. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, each label string equal to its role name. See `docs/agents/triage-labels.md`.

### Domain docs

Multi-context: `CONTEXT-MAP.md` at the repo root points at one `CONTEXT.md` per bounded context, each with its own `docs/adr/`. System-wide decisions live in `docs/adr/`. See `docs/agents/domain.md`.

## Coding standards

评审的 **Standards 轴**从这里进：`docs/agents/coding-standards.md`（`/code-review` 找的就是它）。
规则本身在三个**单一事实源**里——本文件的不变量、`Directory.Build.props`、`.editorconfig`——
那份文件只负责指路、区分硬违规与判断项、并列出评审要对照的那组 baseline 坏味道。

## Design vocabulary

谈设计时用一套固定的词：**模块 / 接口 / 实现 / 深度 / 缝 / 适配器 / 杠杆 / 局部性**。
见 `docs/agents/design-vocabulary.md`。三条判据：**删除测试**、**接口就是测试面**、
**一个适配器只是假设的缝，两个才是真的缝**。

说"这里需要一个接口"之前，先回答：**缝上会变化的是什么？**

## Architecture invariants

These are not style preferences — each one exists because the previous codebase got it wrong. Breaking one is a bug.

1. **A bounded context owns its data.** No context reads or writes another context's tables. No cross-context joins, no EF navigation properties across contexts. Reference other contexts by ID only.
2. **Contexts talk only through `*.Contracts`.** A context must never reference another context's `Domain` or `Infrastructure` assembly.
3. **`*.Domain` depends on nothing.** No EF Core, no ASP.NET, no infrastructure packages, no `Microsoft.Extensions.*` beyond the bare abstractions. If it needs one, the seam is in the wrong place.
4. **One aggregate = one transaction.** Consistency across aggregates and across contexts is eventual, via the outbox.
5. **No service locator.** Constructor injection only. A `static IServiceProvider` holder is forbidden.
6. **No ambient identity generation.** IDs are supplied by the caller or injected as an abstraction — never pulled from a static singleton inside a constructor.
7. **Extract to `BuildingBlocks` only after the second consumer.** Shared code must be proven necessary by two contexts before it moves.
8. **Every host composes itself explicitly.** A service's `Program.cs` shows what that service is made of. No `InitApplication(moduleKey)`-style hidden composition, no `CoreServiceType`-style "which service am I" runtime enum.

**哪条由哪条测试守着**，见 `tests/Architecture.Tests/InvariantCoverage.cs`——那是结构化的表，
不是注释：名字写错、测试被删、或者新增测试忘了登记，都会让
`InvariantCoverage_IsCompleteAndPointsAtRealTests` 失败。

不变量 **4（一个聚合 = 一个事务）没有结构测试**——编译器与程序集引用都管不了它，
行为由事务管线与九个聚合的版本号测试守着。

不变量 **6（ID 不由环境态生成）原本也没有**，而表里当时给的理由是
"结构性保证来自不变量 3 的测试（碰不到基础设施就调不到静态生成器）"——
**那句话是错的**：`Guid.NewGuid()` 与 `DateTime.UtcNow` 都是 BCL，不变量 3 只挡基础设施程序集，
而领域层就在调 `Guid.NewGuid()`（12 处，全在领域事件的 `EventId` 上，那是合法例外）。
评审 20 补了真的结构检查：`DomainAssemblies_MustNotReachForAmbientState`。
**"有理由"和"理由是对的"是两件事，而前者读起来像后者。**

**聚合的版本号**（`AggregateRoot.Version`，见 `docs/adr/0011-optimistic-concurrency-in-the-aggregate.md`）：

> **Version 改变，当且仅当可观察状态改变了。**

改状态的路径写 `return Changed();`（或 `Changed(value)`）；返回 `void` 的写 `BumpVersion();`；
空操作提前返回 `Result.Success()`——**不带** `Changed`。
这样读代码的人不必记住每个方法的语义：**末尾是 `Changed()` 就是改了，是 `Result.Success()` 就是没改。**

空操作若自增，乐观并发会在**没有冲突的情况下**误报冲突，
而误报的代价是调用方开始重试或干脆忽略冲突——那时这个机制就废了，且废得很安静。
**编译器管不了这条**，所以九个聚合每个都有一条"改状态 +1 / 空操作不变"的测试。

**依赖方向：端口在里，实现在外。**

- `*.Domain` 不依赖任何东西（不变量 3）——白名单式检查。
- `*.Application` **不依赖 `*.Infrastructure`**——端口在应用层定义，适配器在宿主组装。
  这条测试**同时查两层**：编译产物（真的用了）与 `csproj`（埋着可以用）。
  **只查编译产物会漏掉后者**：C# 只为**实际用到**的程序集发出 AssemblyRef，
  所以一个没被使用的 `ProjectReference` 在 DLL 里不留任何痕迹——反向验证会通不过。

  这条规则此前不存在，代价是三个消息端口曾住在 `BuildingBlocks.Infrastructure` 里，
  于是 `Auditing.Application` 不得不引用基础设施，而没有任何东西发现它（评审 07、票据 45）。

## Deployment invariants

**业务服务不对外暴露，边缘是唯一入口。**

这条**没有测试守着**——它取决于编排（compose / Aspire / K8s）是否把业务服务的端口发布到宿主机，
而那不在编译期或测试的射程内。写在这里是因为它的失效方式很安静：

边缘上的认证（`requireAuthentication: true` 的路由）**只对经过边缘的请求生效**。
一旦某个服务的端口被发布出去，谁能直连到它，谁就绕过了整套边缘保护——
而所有测试仍然全绿，因为测试从不经过网络拓扑。

推论：编排文件里**只有网关可以发布端口**。参照仓库恰好是这条假设失效的样本——
四个可部署单元共享一个 Core，"网关是唯一入口"从来没有被写下来过，也就没有人能检查它。

## Layout

```
src/
  BuildingBlocks/          shared kernel, proven by ≥2 contexts
  Services/<Context>/      one bounded context: Domain / Application / Infrastructure / Endpoints（模块）
  Hosts/                   可部署的宿主：PlatformHost 组装五个平台模块
  Composition/             宿主装配库：日志与配置中心——六个宿主**一模一样**的那部分
  Gateway/                 YARP edge — routing model + host
tests/
  TestSupport/             共享测试替身：SequentialIdGenerator / FixedClock / MutableClock
aspire/                    AppHost + ServiceDefaults（本地编排；**服务不依赖它**，见 ADR-0005）
```

**测试替身走 `tests/TestSupport`，不进 `BuildingBlocks`。** 不变量 7 说的是**产品代码**
（"被第二个上下文证明需要才上移"）；`BuildingBlocks` 里放的是运行时契约，
而 TestSupport 是测试装置，两者不该混在一起。每个替身**只允许有一份定义**。

**例外：`Program.cs` 里的 `Failure(Error error)` 各服务各写一份**，这是决定而非遗漏——
那 6 行是每个服务**自己的 HTTP 契约**，不同服务对"错误码 → 状态码"的映射会不同。
统一它们等于用一个共用函数锁死五个服务的 HTTP 语义。

每个上下文自带 `CONTEXT.md` 与 `docs/adr/`。当前五个上下文都是**平台能力**（通用/支撑子域）：
它们由一个宿主 `src/Hosts/NexusStackNext.PlatformHost` 组装、共用一个数据库（库内按 schema 分开）。
**未来的业务上下文才各自独立成服务、独立库**——那才是需要按业务边界切分的地方。见 ADR-0013。

## Build & test

```powershell
dotnet build NexusStackNext.slnx     # 全量构建
dotnet test NexusStackNext.slnx      # 全部测试（含架构不变量测试）
```

### 三段自动化检查，顺序固定

`typecheck → tests → format`（`resolving-merge-conflicts` 要求的三段，缺一段就等于没有这一段）：

```powershell
dotnet build NexusStackNext.slnx              # ① 类型与警告（TreatWarningsAsErrors）
pwsh -File scripts/run-tests.ps1              # ② 测试（**串行**，见下）
pwsh -File scripts/check-format.ps1           # ③ 格式（dotnet format --verify-no-changes）
```

**第三条不是摆设**：第一次跑它时全仓有 **448 处**格式违规（含两个 CRLF 文件，而 `.editorconfig`
写的是 LF），而**构建全绿**——"声明了风格"与"检查了风格"是两件事。
修格式用 `scripts/check-format.ps1 -Fix`。

### 跑全量请用脚本，不要直接 `dotnet test <解决方案>`

```powershell
./scripts/run-tests.ps1                                      # 全量
./scripts/run-tests.ps1 -Filter 'FullyQualifiedName~SomeTest' # 过滤
```

`dotnet test <解决方案>` 会**并行**跑十几个测试工程，而它们**全部连同一台 PostgreSQL**。

2026-09-30 的实测后果：13 个工程同时建表删表 → `pg_catalog` 膨胀 → checkpoint 从毫秒级
变成 **336 秒**（单个 fsync 最长 8.7 秒）→ 数据库不可用 → **AgileConfig 连不上库直接崩**
→ 容器的 `restart` 策略把它无限拉起 → 崩溃循环把磁盘读吃满 → **最后 SSH 都卡死**。

**注意 `-m:1` 不管用**——它限制的是构建并行度，不是测试宿主的启动并行度。
实测加上它之后仍然同时起 13 个进程。想真正串行只能自己循环，脚本就是这么做的
（实测并发峰值 = 1）。

脚本另外做了三件事：打印**目标数据库主机**、**全局互斥**（同时只允许一份全量在跑）、
以及**逐工程打印耗时**（排查"哪一类慢"时最缺的就是这个）。

**`env/test.dev` 里那台库不只我们在用**——它同时是配置中心的库。
**跑之前先确认那台机器上还有谁。** 这不是测试的错，是环境的事实，
但测试代码里没有任何地方体现它，所以写在这里。

跑起来只有**两个进程**：平台宿主与网关。

**平台宿主**（`127.0.0.1:5191`）：

- `/health`、`/health/live` —— 进程还能应答（**不查依赖**）
- `/health/ready` —— 依赖可用（**查**）
- `/openapi/v1.json` —— 它自己的 OpenAPI 文档（总是开，纯数据）
- `/swagger` —— API 参考界面（**仅 Development**：它会绕过边缘暴露全部 API 面）

**网关**（`127.0.0.1:5190`）：

- `/health`、`/health/live`、`/health/ready` —— 同上；`ready` 还查它的 cluster 是否可达
- `/openapi/v1.json` —— **聚合文档**：网关自己 + 每个后端
- `/openapi/gateway.json` —— 只含网关自己的（内部路径，聚合器取它）
- `/gateway/openapi/sources` —— 各来源是否取到、各多少条路径
- `/swagger` —— API 参考界面（**总是开**：它就在边缘上，这正是要暴露的地方）

`ready` 只在**真正有依赖**的地方有区别：网关查它的 cluster 是否可达，Files 模块查存储是否可写可删。
其余四个模块的存储是内存适配器，所以对它们而言 `ready == live` 是事实，不是偷懒。

**注意 Files 的两种存储故障走的是不同路径**：启动时路径不可创建 → **进程直接崩**（快速失败，就绪检查根本来不及报）；
运行期存储掉线 → **就绪检查报 503**。两者互补，不是重复。

```powershell
dotnet run --project src/Hosts/NexusStackNext.PlatformHost  --urls http://127.0.0.1:5191
dotnet run --project src/Gateway/NexusStackNext.Gateway       --urls http://127.0.0.1:5190
```

### 写检查与做验证的纪律

这一节是**一次一次踩出来的**，不是想出来的。每条都对应一次真实的误判。

**一、检查没有对象可查时，不得报告通过。**

```csharp
var violations = new List<string>();
foreach (var path in SomeEnumeration()) { ...收集违规... }
Assert.True(violations.Count == 0, "...");   // 枚举为空 → 静默通过
```

这类形状有一个安静的失效模式：**什么都没扫到，违规就是空的，测试通过。**

而它真的发生过：`DomainAssemblies_MustNotReachForAmbientState` 的过滤器一个文件都没匹配上，
于是往领域层塞 `Guid.NewGuid()` 与 `DateTimeOffset.UtcNow` **两次都绿**；
修好过滤器之后，才发现**判据本身也是错的**（例外划得太窄，把合法代码报成违规）。

**所以：源码程序集一律走 `SourceAssemblyPaths()`（带 `Assert.True(Count > 0)`），
新写结构检查时照着它写。** 枚举别的东西（文件、类型、端点）同理。

> 一个"永远通过"的检查，不只自己不工作——**它还会让所有依赖它的判断都失去依据。**

**二、反向验证：不看测试怎么写，看它在你打破规则时红不红。**

一张表说"某条不变量由某个测试守着"，只证明**指针有效**，不证明**测试抓得住**——
指针指向一个空函数也是有效的。

做法：制造一次真实的违反，确认测试红；再还原，确认绿。**两个方向都要。**

**变异的第一步是确认它编译通过。** 本会话里反向验证失败过三次，**三次全是变异本身没编译过**
（`CA1859`、`CS0019`、`CS1591`）——而其中有两次输出是空的，被误读成"测试没说话"。

**三、"没匹配到输出"不等于"没有结果"。**

看测试结论之前，先看**退出码**，再看**有没有构建错误**。
否则"构建失败"与"测试通过"在一段筛选过的输出里长得一样。

**四、集成测试直打宿主时，你测的不是用户走的那条路。**

`WebApplicationFactory` 把请求直接交给宿主，**跳过了网关**。而网关上有一样东西是宿主里没有的：
**路由策略**（`requireAuthentication`）。于是出现过这样一幕：

- 全部测试绿；
- 而**没有任何用户能登录**——路由表里一条前缀路由
  `/api/identity/{**catch-all}` + `requireAuthentication: true`
  把 `POST /api/identity/login` 也吞了进去，登录本身要令牌。
- 边缘是唯一入口（部署不变量），所以那不是"少配一个开关"，是系统对用户关闭。

**发现它的是真实 HTTP 的端到端旅程**（`scripts/verify-user-journey.ps1`）：
注册 401、登录 401，而直连后端 400（说明后端活着）。

两条防线，一贵一贱，都要有：

| | 跑得多快 | 验的是什么 |
|---|---|---|
| `AnonymousEndpointsAreReachableTests`（静态读 `routes.json`） | 每次构建 | "该匿名的端点在路由表里有没有匿名路由" |
| `scripts/verify-user-journey.ps1`（两个真进程 + curl） | 手动 | "用户到底走不走得通" |

**静的那条便宜但不是事实**——它比对路径，不跑 YARP 的匹配；**动的那条是事实但贵**。

**五、写文件的脚本，写之前先确认读到了非空内容。**

`[System.IO.File]::WriteAllText($path, $null)` **不抛异常——它写一个空文件**。
本会话因此往仓库里留过一个 0 字节的 `AuditEntry.cs`，而**构建照样通过**：
C# 编译器对空源文件没有任何意见。没有测试、没有检查会告诉你这件事。
