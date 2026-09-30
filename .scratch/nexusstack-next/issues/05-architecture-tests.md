# 05 — Architecture.Tests：把不变量变成断言

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01

## 说明：为什么不用"每条不变量一个测试"的字面形式

> **2026-09-29 修订（票据 03 期间）**：`BuildingBlocks_MustStayAtTheFoundingSet` 已更名为
> `BuildingBlocks_MustNotGrowBeyondTheDeclaredSharedKernel`。原断言"集合恰好等于创始的那一个"
> 会在每次正常新增共享内核时误报；改为断言实际集合是 ADR-0001 声明的那四个的**子集**。
> 语义更准：不是"不许新增"，而是"不许长出声明之外的东西"。触发这次修订的正是下面说的哨兵——
> 新增 `BuildingBlocks.Application` 时它按设计变红了。

不变量 4（一个聚合一个事务）是**运行时行为**，不是静态结构。用 NetArchTest 或源码扫描去"测"它只能是假测试。
它真正的归属是票据 03 的管线实现与票据 04 的事务边界测试。不变量 6 同理，由 3 的结构保证 + 领域测试的行为保证共同覆盖。

把假测试写出来会让"8/8 覆盖"这句话变得廉价——那正是参照仓库 `MQ-Idempotency-Review.md` 犯过的错：
它宣称"重试路由正确性已通过最终审查"，但审查只覆盖了"进重试队列"，漏了 DLX 回程（review/04 发现 1）。

## Comments

### local

**Answer**

**产出**

- `tests/Architecture.Tests/Architecture.Tests.csproj` —— 引用 `NetArchTest.Rules 1.3.2`（本机 NuGet 缓存已有，无新增网络依赖）+ 全部 src 项目
- `tests/Architecture.Tests/SolutionAssemblies.cs` —— 程序集发现与两种读取方式
- `tests/Architecture.Tests/ArchitectureInvariantTests.cs` —— 8 个测试
- `Directory.Packages.props` 登记 `NetArchTest.Rules`

**8 条不变量的覆盖情况（未覆盖的也写清楚，不留"以为测了其实没测"）**

| 不变量 | 覆盖方式 | 测试 |
|---|---|---|
| 1 上下文独占数据 | **结构性代理**：上下文之间不得互相引用。真正的数据规则由评审与设计保证 | `Contexts_MustNotReferenceOtherContexts` |
| 2 只通过 Contracts 通信 | 同上 | `Contexts_MustNotReferenceOtherContexts` |
| 3 Domain 不依赖基础设施 | **白名单**（不是黑名单）：只允许 BCL + 自己 | `DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList` |
| 4 一个聚合一个事务 | **不是结构规则**，由票据 03 的管线实现。此处不假装测它 | —— |
| 5 禁止服务定位器 | 反射扫描所有 src 程序集的静态 `IServiceProvider` 字段与属性 | `NoServiceLocator_StaticContainerHoldersAreForbidden` |
| 6 ID 不作为环境态生成 | 结构性保证由不变量 3 提供（碰不到基础设施就调不到静态生成器）；行为性保证见 `Aggregate_NeedsNoGlobalState` | —— |
| 7 第二次消费才上移 BuildingBlocks | **近似**：冻结创始集合，加一个就必须改测试，改的时候必须回答"谁需要它" | `BuildingBlocks_MustStayAtTheFoundingSet` |
| 8 宿主显式组装 | 扫描 `src/**/Program.cs` 的禁用装配调用 | `Hosts_MustComposeExplicitly` |

外加两条守护测试：

- `SourceAssemblyInventory_MatchesDeclaredSet` —— **哨兵**。新增 src 项目会先让它红，逼着加的人确认新程序集被哪些规则覆盖、是否让某条规则变成空转。
- `AllSourceAssemblies_AreReferencedByThisTestProject` —— 类型级检查需要真实加载程序集；漏加 `ProjectReference` 会让检查**静默漏测**，这条专门堵这个洞。
- `DomainEntities_MustEncapsulateTheirState` —— 用 NetArchTest 自定义规则（Mono.Cecil）要求实体/聚合根的属性**不得有 public setter**，直接针对参照仓库"23 个实体全是无行为 POCO"的病根。

**反向验证（本票的关键，否则这些测试就是装饰）**

注入四类违规后跑测试：

| 注入的违规 | 结果 |
|---|---|
| 领域层加 `PackageReference Mono.Cecil` | ✅ `DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList` 失败 |
| 领域实体加 `public string Name { get; set; }` | ✅ `DomainEntities_MustEncapsulateTheirState` 失败 |
| 领域层加 `static IServiceProvider ServiceLocator { get; set; }` | ✅ `NoServiceLocator_StaticContainerHoldersAreForbidden` 失败 |
| `src/__ViolationHost/Program.cs` 写入 `InitApplication("violation-probe")` | ✅ `Hosts_MustComposeExplicitly` 失败 |

结果：`失败! - 失败: 4，通过: 4` —— 四条该失败的失败、四条该通过的通过。探针已全部删除。

**回退后确认**

```
dotnet build  → 已成功生成，0 个警告
dotnet test   → 28 + 8 = 36/36 通过
领域层 PackageReference 数量 = 0
```
