# 19 — BuildingBlocks.Infrastructure：EF Core 基座

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 04, 15

> **全部完成。** 六条验收在真实 PostgreSQL 上逐条验证，每条都有反向验证。

## 阻塞点（已解除）

头号验收「聚合写入失败时 Outbox 记录**不**产生（同一事务）」需要**真实 PostgreSQL**。
本机没有容器运行时（ADR-0005），Testcontainers 用不了。请提供一个测试库（票据 15）。

在拿到测试库之前，本票据**不开工**——EF 的事务语义、`ValueGeneratedNever` 与数据库列的对应关系、
软删过滤器翻译成的 SQL，都必须在真库上验证；用 InMemory provider 验证这些只会得到虚假的安全感
（它没有事务，SQL 翻译也完全不同）。

## 一处过期表述（本轮更正）

原文写着"每个上下文独占一个库（ADR-0002）"。
**ADR-0002 已被 ADR-0013 取代**：一个共用库、库内按 schema 分开。
下面第 1 项按 ADR-0013 实现。

## 要做什么

1. **DbContext 基座**：一个库、每个上下文一个 schema（ADR-0013），迁移只有一套。
2. **主键约定**：`ValueGeneratedNever()`，数据库列**不带** IDENTITY（ADR-0009）。
   参照仓库两个权威并存，序列停在 1，与种子 admin 撞车。
3. **软删除全局过滤器**：沿用参照仓库 `ReplacingExpressionVisitor` 的做法（保留清单 #5）。
4. **审计拦截器**：`SaveChangesInterceptor`（保留清单 #6），且**所有写入路径都必须经过它**——
   这是 ADR-0008 的直接要求：参照仓库的批量库绕过了审计，一条本该留痕的批量删除在审计表里毫无记录。
5. **`IUnitOfWork` 实现**：**执行策略包裹事务**（策略见 `IUnitOfWork` 文档注释，票据 03 已定）。
   参照仓库开了 `EnableRetryOnFailure()` 又用裸 `BeginTransactionAsync()`，那条用例必然失败。
6. **Outbox / Inbox 的 EF 存储实现**：`OutboxEntry` 与聚合写入**同一事务**；
   `TryBeginProcessingAsync` 与业务改动**同一事务**（事务性收件箱）。
   唯一索引：`(ConsumerName, EventName, MessageId)`。

## 验收标准

- [x] 测试：聚合写入失败 → Outbox 记录**不**产生（同一事务，真库）。
- [x] 测试：同一消息消费两次，业务只生效一次（真库，唯一索引 + 事务）。
- [x] 测试：`Id` 列不带 IDENTITY，且由应用侧赋值（检查建表 DDL 与插入行为）。
- [x] 测试：绕过 `SaveChanges` 的批量删除会被审计拦截器发现，**或该 API 被禁用**（ADR-0008）——本票选**构建期禁用**，见文末。
- [x] 测试：软删记录不出现在常规查询里，但仍在表中。
- [x] 测试：`IExecutionStrategy` 与事务可以共存（不再出现"执行策略不支持用户自建事务"）。

## 进度：已完成的部分

| 东西 | 位置 |
|---|---|
| 上下文基类 | `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/NexusStackDbContext.cs` |
| 选项扩展（schema + 迁移历史 + 重试 + 模型缓存键） | `Persistence/NexusStackDbContextOptionsExtensions.cs` |
| schema 感知的模型缓存键 | `Persistence/SchemaAwareModelCacheKeyFactory.cs` |
| 集成测试（4 条） | `tests/BuildingBlocks.Infrastructure.IntegrationTests/` |

### 三条约定，各自的**反向验证**都做了

| 约定 | 摘掉它之后 |
|---|---|
| 主键 `ValueGeneratedNever` | `PrimaryKey_IsAssignedByTheApplication_NotTheDatabase` **红** |
| 软删全局过滤器 | `SoftDeletedRow_IsHiddenFromQueries_ButStillInTheTable` **红** |
| schema 参与模型缓存键 | 4 个里红 1 个——**正是那个依赖执行顺序的** |

### 迁移历史表的位置

`HasDefaultSchema` **不会**把 `__EFMigrationsHistory` 一起 schema 化，
它落在连接串的默认 schema（通常是 `public`）。所以五个上下文会共用一张历史表，
"每个上下文各自迁移"从结构上就不可能——第二家跑迁移时会看到第一家已经建过的表。
本基座显式传 schema，于是"一个库五个 schema"与"五个库"**只靠换连接串就能切换**。

## 这一步实测踩到的三个坑（都不是推理出来的）

### 一、约定跑在实体注册之前，等于没跑

第一版把 `ApplyNexusStackConventions()` 放在 `base.OnModelCreating` 里，
而派生类**在那之后**才 `Entity<T>()`。于是约定执行时模型里一个实体都没有，
`ValueGeneratedNever` 一条都没生效——**主键列照样长出了 IDENTITY，编译器不报错**。
只有把建表 DDL 打出来看才会发现。

**修法不是调顺序，而是让顺序不可能写错**：基类把 `OnModelCreating` 接管并 `sealed`，
派生类只实现 `ConfigureModel`，约定的位置由基类保证。

### 二、`EnsureCreatedAsync` 在库已存在时什么都不做

本仓共用一个库（ADR-0013），所以库**永远存在**——`EnsureCreated` 于是不建表、不报错、
直接返回，失败出现在**后面的第一次查询**上（`42P01: relation ... does not exist`），离原因很远。
改用 `IRelationalDatabaseCreator.CreateTablesAsync()`。

### 三、schema 当构造参数会让模型缓存出错

EF 默认按 **DbContext 类型**缓存模型，而 `HasDefaultSchema` 是烘进模型里的。
四个用例各自用独立 schema 时，**只有第一个的 schema 进了模型**——
单独跑全绿，一起跑就红。这个缺陷的症状看起来像"测试隔离没做好"，实际是缓存键不对。

修在基座里（而不是在测试里绕开）：schema 是基类的构造参数，那就得按"它可以变"来处理。
生产里每个上下文的 schema 是常量，缓存行为与原来完全一样。

## 完成（第二轮）

第 4、5、6 项与其余四条验收也已完成——共 **15 条集成测试**，全部真库跑通。

## 还剩什么（已无）

第 4、5、6 项（审计拦截器、`IUnitOfWork`、Outbox/Inbox 存储）与验收 1、2、4、6。
它们互相咬合：验收 1「聚合写入失败 → Outbox 不产生」需要
**领域事件拦截器**与 **`IUnitOfWork` 的事务**同时在位，所以这三项应当一起做。
