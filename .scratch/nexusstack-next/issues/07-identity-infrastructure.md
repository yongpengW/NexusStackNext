# 07 — Identity 基础设施：映射、迁移与仓储

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 19

> **2026-09-29 更新阻塞关系**：票据 04（消息基座）已完成，但本票真正依赖的是 **票据 19（EF Core 基座）**，
> 而 19 阻塞在票据 15（测试库）。因此本票在拿到真实 PostgreSQL 之前**不开工**——
> 迁移、`ValueGeneratedNever` 与唯一索引都必须对着真库验证。

## 要做什么

`src/Services/Identity/NexusStackNext.Identity.Infrastructure`：

- EF Core 映射：`ValueGeneratedNever()` 主键（ADR-0009，修原项目双权威缺陷）。
- **唯一索引**：用户名、手机号、邮箱、`RoutePattern + RequestMethod`（原项目**零**唯一索引）。
- **CHECK 约束**与**并发令牌**（原项目零 CHECK、更新为全列覆盖，无乐观并发）。
- 迁移：~~本上下文独占 `nexus_identity` 库~~ → **库内 `identity` schema**（ADR-0013 取代了 ADR-0002）；**只有一套迁移**（原项目迁移版本号三处不一致：
  快照 10.0.3 / 脚本 10.0.7 / 包 10.0.12）。
- 仓储：只暴露真正被用例需要的接口，**不做** 64 成员的浅穿透泛型仓储（原项目 10 个成员零调用）。
- 读路径保留 Mapster `ProjectToType`（保留清单 #4）；查询对象保留 Ardalis.Specification（保留清单 #3）。
- 软删除全局过滤器（保留清单 #5）+ 审计拦截器接入（保留清单 #6）。

## 验收标准

- [x] 迁移可对空库执行成功，且 `Id` 列**不带** IDENTITY（对照 `SqlMigration/InitDatabase.sql:9` 的反例）。
- [x] 唯一索引存在，且有测试证明重复用户名插入失败。
- [x] 有一个测试证明：绕过仓储直接 `ExecuteDelete` 会被审计拦截器**发现**（或明确禁止该 API 的使用，由票据 05 断言）——**票据 19 已完成**：源码级守卫 `AuditBypassIsForbiddenTests` 在构建期禁用了那组 API，且带反向验证。
- [x] 仓储接口的成员数明显小于原项目的 64（写进 PR 描述，说明每个成员的实际调用方）——现有端口共 **8** 个成员（IUserRepository 3 / IRoleRepository 3 / IApiResourceRepository 2），对照原项目 64。
- [x] 全部集成测试针对真实测试 PostgreSQL 运行（票据 15）。

## 证据

- `review/02`：主键双权威；0 唯一索引 / 0 CHECK / 0 并发令牌；迁移版本号三处不一致；仓储 64 成员且 10 个零调用；
  `ServiceBase.cs:40,56` 的 `ChangeTracker.Clear()` 静默丢弃同请求内其它改动（新实现禁止此做法）。

## 进度：映射、迁移与三处约定已完成

### 已交付

| 东西 | 位置 |
|---|---|
| 值转换器（强类型 ID 与值对象） | `Identity.Infrastructure/Persistence/IdentityValueConverters.cs` |
| 上下文与映射 | `.../IdentityDbContext.cs` |
| 设计时工厂 | `.../IdentityDbContextFactory.cs` |
| 迁移 | `.../Migrations/`（一套，`InitialIdentity`） |
| 集成测试（3 条） | `tests/Identity.IntegrationTests/` |

**数据库的现状**（这是本票第一次让它长出常驻表）：

```
nexusstack_platform
  └── identity
        __EFMigrationsHistory   api_resources   inbox
        menu_nodes   menu_trees   outbox
        refresh_tokens   roles   users
      唯一索引 6 个：ux_users_user_name / ux_users_email / ux_users_phone
                     / ux_roles_code / ux_api_resources_route_method / ux_refresh_tokens_hash
```

### 这一步实测踩到的四个坑

**一、只在构造函数里赋值的 get-only 自动属性，背衬字段是 `readonly`——而 EF 的约定不映射 readonly 字段。**

于是 `Role.IsSystem`、`User.IsBuiltIn`、`RefreshToken.IssuedAt/ExpiresAt` 这些
"创建后不可变"的字段**静默地不进模型**，直到构造函数绑定报
"Cannot bind 'isSystem' ... only mapped properties can be bound" 才被发现。逐个显式声明。

**二、CHECK 约束里的列名不受任何建模期校验。**

我按 snake_case 写了 `http_method = upper(http_method)`，而 EF 建出来的列是带引号的
`"HttpMethod"`——错误只在迁移真的跑在库上时报（`42703: column "http_method" does not exist`），
而那时它看起来像"表建错了"。

**三、EF 的原始集合只接受 `List<T>` 与数组。**

`HashSet<RoleId>` 与 `IReadOnlyList<MenuId>` 两种形状都被拒，而背衬字段是 readonly 也换不掉。
角色分配与菜单授权两处集合**暂不映射**（写明了理由），落到票据 11 / 33——
那里才知道查询是"某人有哪些角色"还是"谁拥有这个角色"，而那决定用数组列还是关系表。
**现在猜一个形状，将来要改的是数据而不只是代码。**

**四、EF 生成的迁移过不了本仓的分析器基线。**

`IDE0005` / `CA1861` / `IDE0161` 都会报错。正解是**在 `.editorconfig` 里把
`Migrations/*.cs` 声明为生成代码**（限定一个目录，理由是代码的来源），
而不是放宽全局基线——后者会连手写代码一起放过。

### EF 仓储（本轮完成）

`EfUserRepository` / `EfRoleRepository` / `EfApiResourceRepository` + `AddIdentityEntityFrameworkStorage`。
内存版**保留**——两者是同一个端口的两个适配器，不是"临时与正式"。

**一处必须在注释里说清的语义差异**：内存版保存的是**聚合实例本身**（改动自动可见），
EF 版需要 `SaveChanges`。所以 `AddAsync` 里直接保存（端口说的是"保存新用户"，
而内存版立刻生效——让两者行为不同是端口最该避免的事），
而"读取 → 改 → 保存"那条路径要经 `IUnitOfWork.SaveChangesAsync`。

**这一步踩到的坑**：两个测试类并行执行时互相 `DROP SCHEMA identity`——
迁移的 schema 名是**编译进去的**，无法按实例参数化，所以这组测试只能操作那一个真实 schema。
失败看起来像"映射写错了"。加 `[assembly: CollectionBehavior(DisableTestParallelization = true)]` 解决。

仓储测试 6 条 + 持久化测试 3 条，全部真库通过。
