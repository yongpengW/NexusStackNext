# 15 — 测试工程与测试库接线

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 04, 05

## 阻塞点（需要你提供）

本机**没有容器运行时**（Docker / Podman / WSL 均未安装），Testcontainers 不可用（ADR-0005 后果）。
集成测试需要**一个真实可用的 PostgreSQL**（远端即可）。请提供一个测试库，或授权在现有 PG 上新建 database。

## 要做什么

四层测试（`spec.md` §8）：

1. `Identity.Domain.Tests` — 聚合不变量，纯内存、无 IO。**原项目这一层根本写不出来**（`new User()` 就抛）。
2. `Identity.Application.Tests` — 命令/查询 Handler + 内存仓储。
3. `Architecture.Tests` — 见票据 05。
4. `Identity.IntegrationTests` — 真实测试库，**每个测试独立 schema + 事务回滚**，避免相互污染。

## 验收标准

- [x] **跑通全部测试工程** —— 但形式**已被票据 66 取代**：`dotnet test <解决方案>` 会并行起十几个工程、
      打崩共享的那台 PostgreSQL（实测把 AgileConfig 一起拖进了崩溃循环），所以现在的权威形式是
      `scripts/run-tests.ps1`（串行 + 全局互斥）。实测：**21 个工程全部通过**。
- [x] 集成测试可重复运行且相互隔离 —— **"并发跑两次"那一半同样被票据 66 取代**：本仓现在**刻意串行**。
      隔离靠"每个测试用实例建一个独立 schema，用完 `DROP … CASCADE`"；Identity 那边还会整 schema 重建，
      因此它的 `AssemblyInfo.cs` 关掉了并行。
- [x] 领域层测试**无任何** IO 依赖（无数据库、无 Redis、无时钟真实调用）——
      6 个 `*.Domain.Tests` 的工程引用只有自己的 Domain + `BuildingBlocks.Domain` + `TestSupport`，
      包只有 xunit / Test SDK / coverlet：**没有 Npgsql、没有 EF、没有 IntegrationSupport**。
- [x] 有一个测试证明：故意让集成测试写脏数据，回滚后库是干净的 ——
      `UnitOfWorkTests`（**真库**：提交确实落库、异常确实回滚，表里仍然只有第一条）
      与 Identity 的 `FailedCommand_LeavesNothingBehind`。
- [x] 测试失败时的输出能直接定位到断言（不是"某个 async 任务取消了"）——
      断言几乎都带消息（例如"用尽档位的消息没有出现在死信队列里"、"src/ 下一个 `*.csproj` 都没找到——
      这组检查等于没跑"），运行器逐工程打印输出与耗时。**这条是五条里最弱的**：它是判断，不是能自动判定的东西。

## Comments

### local

**结票时漏打勾，2026-09-30 补齐（第 3 轮）**

这张票很早一轮就置为 `resolved`，而**验收框一个都没打勾**——`check-tracker` 不检查这件事，
所以它一直没人看见，直到有人问"还有没有待完成的票据"，按"已 resolved 却留着 `- [ ]`"去筛才浮出来。

逐条对证据核了一遍：**三条已满足、两条被后续的票据 66 取代**——
"`dotnet test` 一次跑通"与"并发跑两次结果一致"这两条，与后来"绝不能并行跑共享库"的决定**直接冲突**。
取代不等于"没做"，但也不能就这么打勾，所以两条都写明了取代者。

> **这处盲区现在有检查了。** `check-tracker` §24：
> 已 `resolved` 的票不得留未打勾的验收框——这条检查就是被这两张票逼出来的
> （另一张是 `08-identity-application.md`，差 1 个框）。

## 证据

- 全仓评审一致结论：**原项目零测试工程**（4 份报告均提到）。
- `review/01`：实体无法在进程外构造 ⇒ 单元测试从结构上不可行。

## 已完成（用户提供了远端 PostgreSQL）

**阻塞点解除**：`<服务器IP>:5432` 可达，且建了 `nexusstack_platform` 库
（与 ADR-0013、配置中心里已有的 `PostgreSQL` 键一致）。

### 交付物

| 东西 | 位置 |
|---|---|
| 夹具 | `tests/IntegrationSupport/PostgresTestDatabase.cs` |
| 跳过特性 | `tests/IntegrationSupport/PostgresFactAttribute.cs` |
| 夹具自己的契约测试 | `tests/IntegrationSupport.Tests/`（4 条） |
| 环境变量骨架与启动脚本 | `env/test.dev` + `scripts/run-tests.ps1` |

### 隔离怎么做的

每个测试用实例建一个 `test_<随机>` schema，用完 `DROP … CASCADE`。
**连接串带 `Search Path`**——未限定的表名自动落进那个 schema，
于是**被测代码不需要知道自己在测试里**。靠"测试里手动加 schema 前缀"的话，被测的就不是生产那段 SQL 了。

两层隔离各有各的用处：schema 管测试之间的边界，事务回滚管单个测试内部的清理。

### 验证

```
第一次跑       4/4 通过
再跑一次       4/4 通过（可重复）
跑完残留 schema 0 个
全量（真实连库）435 条 / 18 个工程 / 0 失败 / 0 跳过 / 退出码 0
```

### 缺配置时的两种行为（有意不同）

| 场景 | 行为 |
|---|---|
| 直接 `dotnet test`，没设变量 | 集成测试**报成"已跳过"并带原因**——新克隆的仓库不会因此变红 |
| `scripts/run-tests.ps1`，没设变量 | **直接失败**——项目自己的路径上不该悄悄跳过一整层测试 |

前者保证新克隆可用，后者保证我们不会在不知不觉中少跑一层。

## 顺带补上的一条守卫

票据验收里写着"领域层测试无任何 IO 依赖"。**今天它成立，但没有任何东西守着它**——
六个 `*.Domain.Tests` 都干净，可谁加一个 `Npgsql` 也不会有东西响。

新增 `TestProjectHygieneTests.DomainTestProjects_MustNotReferenceAnythingThatDoesIo`。
**反向验证**：给 `Identity.Domain.Tests` 注入 `Npgsql` →

```
领域层测试引用了做 I/O 的东西，它就不再是「毫秒级、无外部依赖」的那一层了：
Identity.Domain.Tests → Npgsql
失败! - 失败: 1，通过: 11，总计: 12
```

还原 → 12/12。

它与 `ApplicationAssemblies_MustNotReferenceInfrastructure` 一样**只查 csproj**：
C# 只为实际用到的程序集发出 AssemblyRef，所以查编译产物的话反向验证通不过。

## 踩到的一个坑：一个引用了 xunit 的类库会被当成测试工程

`IntegrationSupport` 引用了 xunit（为了 `PostgresFactAttribute`），于是
**VSTest 把它当成测试工程去起测试宿主**——而它没有测试宿主：

```
源"...IntegrationSupport.dll"的 Testhost 进程已退出，但出现错误
退出码 = 1     ← 即使所有真正的测试都通过了
```

**跑单个工程时完全正常（退出码 0），只有整解决方案一起跑才出现**——而 CI 跑的正是整解决方案。
修法是显式的 `<IsTestProject>false</IsTestProject>`。

## 与"四层"的关系

票据原文的四层里，前三层早已存在（Domain 115 + Application 11 + Architecture 12）。
**第四层 `Identity.IntegrationTests` 随票据 07 落地**——它需要 19（EF Core 基座）先给出
真实的 `DbContext`。本票交付的是**它所需要的接线**，并且已经用夹具自己的契约测试证明接线是通的。
