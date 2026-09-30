# 每个上下文独占一个 PostgreSQL 数据库，放弃多数据库支持

> **2026-09-29 已被 [ADR-0013](0013-platform-capabilities-are-one-host.md) 部分取代。**
>
> 本文“一个上下文一个库”的**前提变了**：五个平台能力（Identity / Platform / Scheduling /
> Auditing / Files）是通用子域，现在合成**一个宿主与一个库**（库内按 schema 分开）。
> 未来真正的**业务上下文**仍然各自独立成服务、独立库——那才是本文要守的场景。
>
> 下面保留原文，因为“只支持 PostgreSQL 单 provider”这条仍然有效；
> 而“一库一上下文”这条的适用范围已经收窄。

原项目宣称支持 PostgreSQL / MySQL / SQL Server，实现方式是在配置里切 provider。
代价是：迁移集要按 provider 各维护一套、provider 特有 SQL 用不上，而且它反过来强化了"所有实体同库"的假设——
`NexusStack.EFCore` 里那些通用仓储与 `Ardalis.Specification` 让跨聚合、跨模块的查询变得毫无摩擦。

决定：只支持 **PostgreSQL 单 provider**，且**每个上下文一个独立数据库**
（`nexus_identity` / `nexus_platform` / `nexus_scheduling` / `nexus_auditing` / `nexus_files`）。
跨上下文不建外键、不做 join，只按 ID 引用。

## Considered Options

- **保留多 provider**：保住模板卖点，但每个上下文都要为三种数据库维护迁移，而 DDD 下"每服务一库"本就与它冲突。
- **单库多 schema**：迁移与备份更简单，但 schema 之间 join 依旧唾手可得，边界会被侵蚀。

## Consequences

- 放弃"换数据库"的卖点。若将来真需要，代价是重写迁移与少量 provider 相关代码，而不是重写领域模型。
- 原 `NexusStack.EFCore` 的 provider 分支、通用 `IRepository` 可大幅裁剪，只保留真正被两个以上上下文需要的那部分。

## 2026-09-29 补充实测：单库多 schema 还有一个**会静默失败**的代价

用户追问"为什么不能一个库、不同表更省事"，于是用一个双 DbContext 探针
（`HasDefaultSchema("identity")` / `("platform")`）真实生成了 DDL。结果**推翻了原先的假设**：

```sql
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (   -- 不带 schema 限定！
  "MigrationId" character varying(150) NOT NULL, ...);
DO $EF$ ... CREATE SCHEMA identity; ... $EF$;
CREATE TABLE identity.users ( ... );
```

**`HasDefaultSchema` 只作用于实体表，迁移历史表仍然是不带限定名的**——
它落在连接的默认 schema（通常是 `public`）里。

后果：**五个上下文共用一个库，就会共用同一张 `public.__EFMigrationsHistory`。**

- 可读性：一张表里混着五个服务的迁移记录，**看不出哪个服务在哪个版本**；
  删掉某个上下文的 schema 还会留下孤儿行。
- **更糟的是它会静默跳过迁移**：迁移 ID 是 `时间戳_名称`（实测
  `20260929104243_Initial` / `20260929104247_Initial`）。两个上下文若在**同一秒**里
  生成**同名**迁移，ID 就完全相同——第二个 `Migrate()` 会认为"已经应用过"而**跳过它**。
  没有报错，schema 就是少了一张表。

也就是说：单库方案不是"少一点隔离"，而是**多一处必须手工配置、配错了还不报错的地方**。

### 但这条实测也给出了一个让决定可逆的做法

无论最终选一库还是五库，每个上下文都应当显式写：

```csharp
optionsBuilder.UseNpgsql(connectionString, options =>
    options.MigrationsHistoryTable("__EFMigrationsHistory", "identity"));
```

配了它，**同一套代码在"一库五 schema"与"五库"之间只需改连接串**——
迁移历史各归各位，互不干扰，而且两个方向都安全。

反过来，如果不配，就只能在"五库"这一种拓扑下是对的。**所以这条配置本身与选哪个方案无关，是纯粹的保险。**
