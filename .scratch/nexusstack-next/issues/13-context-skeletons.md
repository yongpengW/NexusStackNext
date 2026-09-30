# 13 — 其余四个上下文骨架

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 01, 02

> **2026-09-29 范围调整**：「每个上下文有独立库与独立迁移」依赖 EF Core 基座（票据 19），
> 而 19 阻塞于测试库（票据 15）。该项**移至票据 19**——它本来就该在那里统一建立迁移模式，
> 而不是在四个上下文里各写一遍。本票交付其余部分。

## Comments

### local

**Answer**

**产出（12 个源码文件 + 4 份词表 + 36 个测试）**

| 上下文 | 项目 | 领域内容 |
|---|---|---|
| Platform | `Domain` + `Api` | `GlobalSetting` + `SettingKey`（分组成结构，不是字符串前缀） |
| Scheduling | `Domain` + `Application` + `Api` | `ScheduledTask` + `ScheduleRunner` + `SchedulingWorker`（**真实后台服务**） |
| Auditing | `Domain` + `Api` | `AuditEntry`（只写不可改） |
| Files | `Domain` + `Api` | `StoredFile` + **两个窄端口** `IFileStore` / `IFileUrlProvider` |

**验收标准**

| 验收项 | 结果 |
|---|---|
| 4 个上下文各自 `dotnet run` 能起来，健康检查通过 | ✅ 四个宿主实跑，`/health` 全部 `HTTP 200 Healthy` |
| 每个上下文有独立库与独立迁移，且无跨库外键 | → **移至票据 19**（见上方范围调整） |
| 每个上下文有 `CONTEXT.md`，词表无实现细节 | ✅ 4 份词表，只含术语与 `_Avoid_` |
| Scheduling 的后台服务能真的触发任务并留下记录 | ✅ 实跑 35 秒后日志出现 `调度一轮：检查 1 个，触发 1 个，跳过 0 个` |
| Auditing 不含任何对外业务查询端点 | ✅ 实跑：`GET /api/auditing/entries` → **404** |
| 4 个上下文都不引用 Identity 的 `Domain`/`Infrastructure` | ✅ 架构测试 `Contexts_MustNotReferenceOtherContexts` **首次非空转**并通过 |

**运行时行为验证（不是"编译通过"，是实跑）**

```
Platform    pid=…  存活=True  /health -> HTTP 200 Healthy
Scheduling  pid=…  存活=True  /health -> HTTP 200 Healthy
Auditing    pid=…  存活=True  /health -> HTTP 200 Healthy
Files       pid=…  存活=True  /health -> HTTP 200 Healthy

GET /api/auditing/entries                              -> 404   （只写，无业务查询端点）
GET /api/platform/settings/identity.token.lifetime     -> 200   （合法键）
GET /api/platform/settings/notakey                     -> 400   （非法键被领域拒绝）
GET /api/files/validate-name?name=..%2Fetc%2Fpasswd    -> 400   （目录穿越被领域拒绝）
GET /api/files/validate-name?name=report.pdf           -> 200
```

**三处针对参照仓库具体缺陷的设计**

1. **Scheduling 不是空壳。** 参照仓库的 `PlanTaskService` 因种子服务注册被注释
   （`Core/ServiceCollectionExtensions.cs:262`）而永远静默跳过，同时以 1Hz 空转——
   每轮新建一个 DI Scope、发一次 Redis GET，什么也不做。
   新实现把**循环**留在薄薄的 `BackgroundService` 里，把**一轮做什么**放进
   `ScheduleRunner`（可确定性测试），并守住"每次执行必须推进下次时刻"。
2. **Auditing 只写。** 不给它开查询端点不是"还没做"，是设计：审计是证据，不是业务数据源；
   一旦开个口子，就会有人拿它做业务查询，然后它变成第二个数据库。
3. **Files 的端口按能力切分。** 参照仓库的 `IFileStorage` 有 14 个成员，
   而**没有任何一个实现支持全部成员**——`AliyunFileStorage.GetAbsolutePath` 抛
   `NotImplementedException`，偏偏视频上传路径会调到它。新设计里 `IFileStore` 只有 3 个方法
   （读写删），签发 URL 是**另一个能力**：做不到的存储不实现它，也就没有"声明了却抛异常"的中间状态。
   有一条测试专门防它重新长回去。

**反向验证（代码变异）**

删掉 `MarkExecuted` 里的 `NextRunAt = at + Interval`——这正是"空转"的成因：

```
失败! - 失败: 3，通过: 33
  ✗ MarkExecuted_AdvancesNextRunAtFromTheActualRunTime_NotTheSchedule
  ✗ ScheduleRunner_SecondRunAtInstead_…（同一时刻第二轮不应再触发）
  ✗ ScheduleRunner_TriggersOnlyDueTasks_AndLeavesARecord
```

三条全中，已还原。

**顺带修掉的一个测试自身缺陷**

`Hosts_MustComposeExplicitly` 在 Platform 出现后**变红**——因为它的源码扫描把注释也算进去了，
而 Platform 的 `Program.cs` 注释里写着"没有 `InitApplication(moduleKey)` 那种入口"。
扫描应该只看代码，不看散文。已改为先剥离注释；字符串字面量**故意不剥离**（保守：宁可误报一次，
也不要因为"它只是个字符串"而漏掉真正的调用）。

**当前状态**：构建 0 警告 0 错误；测试 **247/247**；解决方案 **19 个项目**。

**后续**：骨架阶段的测试放在 `tests/Contexts.Tests` 一个工程里，避免为"每个上下文一个空测试工程"
付出四个项目的成本。**当某个上下文开始有真实业务时，它应当拆出独立的测试工程**——
这条迁移路径是与本票一起定下的，不要等到测试文件长到没法看才想起来。
