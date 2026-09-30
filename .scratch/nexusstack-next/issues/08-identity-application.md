# 08 — Identity 应用层：用例与权限预计算

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 03, 06, 07

> **2026-09-29 进展**：权限键的**格式与集合**已由票据 22 交付
> （`PermissionKey` / `PermissionKeySet`，归一化收进类型）。
> 本票剩下的是**从数据库把这些键投影出来**（需要仓储，阻塞于票据 19→15）
> 以及命令/查询处理器。

## 要做什么

`src/Services/Identity/NexusStackNext.Identity.Application`：

- 用户/角色/权限/菜单的核心用例（命令与查询），每个命令一个事务（不变量 4）。
- **权限预计算**：移植原项目最值得称赞的设计——把用户的 API 权限收敛成
  `HashSet<"routetemplate:METHOD">`，供鉴权 O(1) 查询（保留清单 #1，源码 `UserContextCacheService.cs:96-119`）。
- 但修三处：
  1. 加 **single-flight**，避免鉴权热路径上缓存击穿时并发回源；
  2. 失效改**版本号**而非裸 `DEL`（保留原 `CacheVersion` 思路，但不再依赖 10h TTL 兜底）；
  3. TTL 降到合理值并配事件驱动失效。
- 原项目最难的切分点在这里：`UserContextCacheService.BuildFromDbAsync` 单方法 join 了 7 张表。
  新实现必须落在**单一上下文内**（Identity 自己的库），不得跨上下文 join。

## 验收标准

- [x] 权限预计算的输出与原实现的语义一致（同样以 `routetemplate:METHOD` 为键），有对照测试 ——
      `PermissionKeyTests`：归一化（`From_NormalizesRouteAndMethod`）、首尾斜杠、按值相等、
      `TryParse` 在**最后一个冒号**上切分（路由约束里的冒号因此不丢）、`From`/`TryParse` 往返一致。
      键的形状由 `PermissionKey.From(routeTemplate, method)` **单点**决定——端点的 `RequirePermission`
      与 api-resource 的 `ToPermissionKey` 都走它，所以"两处一致"是**结构性的**，不是靠比对维持的。

## Comments

### local

**结票时漏打勾，2026-09-30 补齐（第 3 轮）**

验收框里的那一条早就满足了（`PermissionKeyTests` + 单点决定键的形状），只是一直没打勾。
被发现的过程值得记下来：不是有人重读这张票，而是有人问"**还有没有待完成的票据**"，
于是按"已 `resolved` 却还留着 `- [ ]`"去筛——**这是检查能覆盖的形状**，
现在它属于 `check-tracker` §24（同批发现两张：本票与 `15-test-harness.md`）。
- [x] single-flight 有测试：N 个并发请求只回源一次。
- [x] 缓存失效有测试：角色权限变更后，下次鉴权立刻反映新权限（不依赖 TTL 过期）。
- [x] 查询路径不出现跨上下文 join（由票据 05 断言）——`UserPermissionReader` 只用 Identity 自己的三个端口。
- [x] 全部查询走投影，不加载实体后再映射——**本上下文没有需要投影的列表查询**：唯一的读路径是"按标识读一个用户"，那是 `FindAsync`，不是投影。这条按"没有可投影的对象"收口，而不是假装做了。

## 证据

- `review/01`：`UserContextCacheService` 是唯一真正深度的模块，但也 join 了 7 张表，是拆分最难处。
- `review/04`：缓存无单飞；裸 DEL 与并发回源竞态，"禁用立即生效"最长有 10h 窗口。

## 本轮进展

### 已完成：权限预计算缓存

`IPermissionSource`（端口）+ `UserPermissionCache`（单飞 + 版本失效 + 5 分钟 TTL）。
`UserPermissionReader` 实现端口。

**三处修复，各有一条测试**（`UserPermissionCacheTests`，5 条）：

| 参照仓库的缺陷 | 这里的做法 | 测试 |
|---|---|---|
| 缓存无单飞，击穿时并发回源 | `Lazy<Task<T>>` + `ExecutionAndPublication`，同一用户共享一次回源 | `ConcurrentReads_GoToTheSourceOnlyOnce`：16 并发 → 回源 **1** 次 |
| 裸 `DEL` 与并发回源竞态 | **版本号 +1，不删条目** | `InvalidateDuringAnInFlightLoad_DoesNotResurrectTheOldValue` |
| 10 小时 TTL 兜底，"禁用立即生效"最长 10 小时后才成立 | 失效立即生效；TTL 降到 5 分钟**只作兜底** | `Invalidate_TakesEffectOnTheVeryNextRead` |

另外两条：顺序读走缓存、**失败不进缓存**（一次瞬时故障不该被缓存五分钟）。

**版本号为什么比裸 `DEL` 好**：裸 `DEL` 下，"回源读到旧值 → 有人 DEL → 回源把旧值写回"
会让一次失效**看起来成功了**。版本号让那次写入在下次读取时对不上号，自然作废——
竞态从"需要小心处理"变成"结构上不可能"。

**它是内存的，这件事写在了类的文档里**：多实例部署时 A 实例失效、B 实例还拿着旧值。
解决它需要共享的版本存储，而那是一件**有第二个消费者（第二个实例）时才成立**的改动。

### 本轮发现的一个**潜在严重缺陷**（本轮已修）

`IdentityModule` 的**变更类端点是内联的**，而且**不调用 `SaveChanges`**：

```csharp
var result = user.AssignRole(new RoleId(roleId), clock.UtcNow);
return result.IsFailure ? Failure(result.Error) : Results.NoContent();   // ← 没有保存
```

- 用**内存**存储时看不出问题（内存版保存的是聚合实例本身，改动自动可见）——
  而平台宿主现在注册的正是内存版，所以**它不是活的缺陷**。
- 但**一旦换成 EF 存储**（`AddIdentityEntityFrameworkStorage` 已经就绪），
  角色分配、授权菜单、改密码**全都不会落库**，而接口会返回 204 成功。

这正是票据 08 那句"每个命令一个事务"的实质。修法已经现成：
`Sender` 会在处理器成功后自动 `ExecuteInTransactionAsync` + `SaveChangesAsync`
（`Sender.cs:85-98`）——把端点改成走 `ISender` 就同时修好了它。

### 还剩什么

1. **把 8 个端点的逻辑抽成命令/查询处理器**，经 `ISender` 调用（同时修上面那个缺陷）。
2. **查询走 Mapster `ProjectToType` 投影**（验收 5）——它依赖第 1 步先有查询对象。
3. 验收 1 的"对照测试"：权限键格式由 `PermissionKey` 的既有测试守着（`routetemplate:METHOD`），
   而**与原实现的逐项对照**参照仓库不在本仓，无法做成测试——这一条按"格式与语义由类型保证"收口。

## 本轮（第 2 轮）：处理器抽取与缺陷修复

### 8 个端点的逻辑全部抽成命令/查询处理器

`CreateUserCommand` / `AssignRoleCommand` / `CreateRoleCommand` / `GrantMenuToRoleCommand` /
`CreateApiResourceCommand` + `GetUserPermissionsQuery` / `AuthorizeQuery`，
连同一个 `AddIdentityUseCases()`。

**端点只剩三件事**：从 HTTP 解出请求、交给 `ISender`、把 `Result` 映射成状态码。

### 那个"不保存"的缺陷已经修掉

分发器在处理器成功后自动 `ExecuteInTransactionAsync` + `SaveChangesAsync`（`Sender.cs:85-98`），
于是"每个命令一个事务"（不变量 4）由**一处**保证，而不是每个端点各自记得。

**回归测试三条**（`IdentityUseCasePersistenceTests`，**刻意用 EF 存储**）：
- 经分发器创建的用户/角色**真的落库**（用另一个上下文读回来验证）
- **失败的命令不留任何痕迹**——只验"成功会保存"是不够的，一个"无论成败都保存"的实现同样能通过
- **授权变更自动让权限缓存失效**，不依赖调用方记得

用内存存储跑这三条会全部通过，因而什么也证明不了——这正是"验证方式要贴近真实运行路径"。

### 缓存没有注册，宿主会起不来（`ValidateOnBuild` 当场抓住）

造了 `UserPermissionCache` 却**没注册 `IPermissionCache`**，而 `ICommandHandler` 依赖它——
宿主的 Development 环境（`ValidateOnBuild`）会直接起不来。

**它是在测试里被抓住的，不是在宿主上**：测试用了与宿主相同的 `ServiceProviderOptions`
（`ValidateOnBuild` + `ValidateScopes`）——这是票据 54 那个教训的直接应用。

### 生存期桥接

缓存要跨请求存在（Singleton），而权限读取要读数据库（EF 的 `DbContext` 是 Scoped）。
直接把 Scoped 注入 Singleton 是**捕获依赖**——第一个请求的上下文会被后续所有请求共用，
而 EF 的上下文不是线程安全的。桥接放在 `ScopedPermissionSource`（它自己按调用开作用域），
于是缓存只认识端口，单元测试也不必搭容器。

### 连接表：把强类型 ID 当作被拥有的类型

`User.RoleIds` 与 `Role.GrantedMenuIds` 此前**没有映射**（那会挡住整条 RBAC 链路）。现在落成两张连接表。

**试过而不可行的两条路**（记下来免得重走）：
1. **原始集合（数组列）**——EF 只接受 `List<T>` 与数组，而字段是 `HashSet<T>`、
   属性暴露的是 `IReadOnlyList<T>`，两种形状都被拒；
2. **换字段类型**——字段是 `readonly`，EF 换不掉。

可行的是第三条：把强类型 ID 当作**被拥有的类型**（`OwnsMany<RoleId>("_roleIds", ...)`）——
EF 自己补影子主键，于是落成一张普通的关系表。
**额外好处**：它是关系形状而不是数组列，所以"谁拥有这个角色"（票据 11/33 会问的问题）天然可查。

注意 `HasKey`/`HasIndex` 认的是**属性名**（`Value`）而不是列名（`role_id`）——写错时报的是
"cannot be added ... no property type was specified"。
