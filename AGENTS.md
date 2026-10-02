# AGENTS.md

## Agent skills

### Issue tracker

Issues, tickets and the map live on **GitHub Issues** (`gh issue ...`); the backend is declared by the first heading of `docs/agents/issue-tracker.md`. `.scratch/<feature-slug>/` is the **frozen archive** of the 2026-09-30 round, not a tracker. See `docs/agents/issue-tracker.md`.

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
  Composition/             宿主装配库：日志与配置中心——两个宿主**一模一样**的那部分
  Gateway/                 YARP edge — routing model + host
tests/
  TestSupport/             共享测试替身：SequentialIdGenerator / FixedClock / MutableClock
aspire/                    AppHost + ServiceDefaults（本地编排；**服务不依赖它**，见 ADR-0005）
```

**测试替身走 `tests/TestSupport`，不进 `BuildingBlocks`。** 不变量 7 说的是**产品代码**
（"被第二个上下文证明需要才上移"）；`BuildingBlocks` 里放的是运行时契约，
而 TestSupport 是测试装置，两者不该混在一起。每个替身**只允许有一份定义**。

**例外：`Failure(Error error)` 各模块各写一份**（住在 `*Endpoints/*Module.cs`，不在 `Program.cs`；
Auditing 不需要它——它的端点直接返回 202/200），这是决定而非遗漏——
那几行是每个模块**自己的 HTTP 契约**，不同模块对"错误码 → 状态码"的映射会不同。
统一它们等于用一个共用函数锁死五个模块的 HTTP 语义。

每个上下文自带 `CONTEXT.md` 与 `docs/adr/`。五个平台上下文都是**平台能力**（通用/支撑子域）：
它们由一个宿主 `src/Hosts/NexusStackNext.PlatformHost` 组装、共用一个数据库（库内按 schema 分开）。
Costing / Pricing 业务样板各自独立成服务、独立库，业务边界见 `docs/costing-pricing-cooperation.md`；平台装配依据见 ADR-0013。

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

基础平台运行两个进程：平台宿主与网关；业务样板另启 Costing / Pricing 宿主。

**平台宿主**（`127.0.0.1:5191`）：

- `/health`、`/health/live` —— 进程还能应答（**不查依赖**）
- `/health/ready` —— 处理业务所需的依赖可用（**查**）
- `/health/logging` —— 来源操作日志、中央审计库与审计消息依赖的独立诊断
- `/openapi/v1.json` —— 它自己的 OpenAPI 文档（总是开，纯数据）
- `/swagger` —— API 参考界面（**仅 Development**：它会绕过边缘暴露全部 API 面）

**网关**（`127.0.0.1:5190`）：

- `/health`、`/health/live`、`/health/ready` —— 同上；`ready` 还查它的 cluster 是否可达
- `/openapi/v1.json` —— **聚合文档**：网关自己 + 每个后端
- `/openapi/gateway.json` —— 只含网关自己的（内部路径，聚合器取它）
- `/gateway/openapi/sources` —— 各来源是否取到、各多少条路径
- `/swagger` —— API 参考界面（**总是开**：它就在边缘上，这正是要暴露的地方）

`ready` 检查真实业务依赖：网关查 cluster，Identity / Platform / Files / Scheduling 在 PostgreSQL 模式下查数据库，Files 还查存储可写可删。
PlatformHost 与 PricingHost 的日志依赖归 `/health/logging`，避免日志探测慢故障让网关摘除可用业务。修改日志采集、就绪分组或告警时先读 `docs/operation-logging.md`。

Identity 默认 PostgreSQL；配置、迁移或重启验证时先读 `docs/identity-persistence.md`。
Platform 也默认 PostgreSQL；配置、迁移、并发写入或重启验证时先读 `docs/platform-settings.md`。
文件迁移与访问读 `docs/private-files.md`；审计迁移与消息摄入读 `docs/committed-auditing.md`；计划迁移、后台触发或交付恢复读 `docs/durable-scheduling.md`。
普通宿主启动不迁移，未迁移或数据库不可用会退出；无库演示须显式选择开发/测试 Memory 模式。
Identity HTTP 持久化测试会创建独立临时数据库，测试账号需具备建库/删库权限，仍按脚本串行运行。

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

**六、"换了环境就好了"必须解释为什么。**

CI 第一次跑就红在一个本机永远绿的地方：检查脚本里三处路径正则写死了 Windows 反斜杠
（`-match '\adr\'`），而 CI 跑在 ubuntu 上——路径用 `/`，于是一个 `adr` 目录都匹配不上，
检查**逐个上下文指控"没有 docs/adr/"：五个冤枉**，而真话是"这条检查看不见对象"。

两件事值得记：

- **枚举为空时不得报告通过，也不得报告违规。** 前者会让一条永远通过的检查废掉自己，
  后者会让一条**看起来在工作**的检查去冤枉正确的代码。修法是同一条：
  报结论之前先证明自己**看得见对象**（现在它报的是"路径匹配没对上（分隔符？大小写？）"）。
- **"本机全绿"与"CI 全绿"是两个命题。** 它们之间的差集不是运气，是**平台差异**：
  路径分隔符、大小写敏感、换行、区域设置、可用工具。所以那三段检查里，
  **第三段（格式）与 CI 是同一份脚本**——而不是"本地跑跑看"。

**七、看到红还提交，是最贵的错误。**

2026-09-30 真实发生过一次：两个检查器都返回退出码 **1**，我打印了失败输出，
然后**继续把提交做了**——理由是"分支上，主干受保护"。

- **它没造成事故，是 `main` 的 ruleset 兜住的，不是我。** 保护规则挡的是"没经 CI 的提交进主干"，
  它不负责"我该不该按下提交"。
- 本文件第三条纪律（"先看退出码，再看结论"）写的就是这件事，而我**看见了退出码、也看见了结论，
  仍然往下走**——问题不在"没看"，在"看了不算数"。
- 可执行的做法：**把三段检查与两次断言写成一条命令，红就停**。
  人工"再看一眼"在赶进度时不可靠——这不是意志问题，是流程问题。

**八、契约一变，先列全"谁失去对象"。**

把票据后端从 markdown 切到 GitHub 时，我列了 8 组会失去对象的检查，**漏了一组**：
"变体声明"那组在断言 markdown 契约的文档锚点（`## Comments`、"面板能读到什么"），
契约一翻它就红了——**它自己成了失去对象的那一个**。

- 教训不是"要更小心"，而是**方法**：按"**它读什么**"去列，而不是按"它叫什么"——
  按名字列会漏掉那些名字看不出来后端的组。
- 处理只有两种：**改写**（义务留在原处，只换载体）或**退役并写明理由**。
  留成空壳 = 假装在工作；一删了事 = 那天切回来时没人检查。

**九、说"某处能读到"，必须指明验的是哪一层。**

切换票据后端时，我把"面板能读到"写成了判据 —— 实际只验了**解析层**
（deck 的 `parseIssueTracker` 返回 `github`/high ✓），而**渲染层**（面板里到底显示什么）
我一次都没看过 ✗。

- **层与层不等价**：解析器对了 ≠ 数据源对了 ≠ 渲染对了；三层的失效方式各不相同，
  而"能读到"这句话把它们糊成了一句。
- 定性的话（"能读到" / "跑得通" / "看得见"）**要么指到某一层，要么就是一句无法证伪的声明**。
- 顺带否掉一个直觉：我猜"deck 绑了旧后端"，读源码与查文件后**被否掉** ——
  它只存内存、TTL 30 秒、不落盘。**直觉要靠读源码与查文件来否证，不是靠再想一遍。**
- 后果落在判据上：那条判据最后**改写**成两个能验的（deck 解析出 `github` + `gh issue view` 读得到），
  "渲染"一条**退役并写明理由** —— 这是第七条纪律（失去对象的判据要处理）在同一天里的第二次应用。

## 提交、PR 与凭据

**分支 + PR 是默认路径**（2026-09-30 起）：`main` 上只出现**已经过 CI 的**提交，日常改动走分支 + PR。
这条约定靠 GitHub 的 **`main` 保护规则**执行——它不在仓库文件里，所以"开了没有"要看
`docs/agents/pr-and-credentials.md` 的现状表。

| agent 直接做 | 必须**先问** |
|---|---|
| 读 CI（`gh run view --log-failed`）、看运行与逐步结论 | 合并 PR |
| 开/看/改 PR（`gh pr create` / `view` / `diff`）、只读 `gh api` | **写或删 secret**、改仓库设置、删分支或 tag |
| 推**特性分支** | 推 `main`、重跑或取消工作流、任何 `--force` |
| 跑本地那三段检查（见上一节） | |

**凭据纪律（硬规矩）**：值只走**文件或 stdin**（`gh secret set NAME < <文件>`），不进命令行；
**任何口令、令牌、连接串都不粘进会话**——粘进去就进了对话记录；读日志与配置时**只报"有没有"**，不回显值。
机器守着的那一半是 `scripts/assert-no-credentials.ps1`（守**模板生成物**），人手里的动作靠上面三条。

PR 的命令、CI 与本地的一致性、以及 gh 与权限的现状，见 `docs/agents/pr-and-credentials.md`。
