# 06 — Identity 领域模型：聚合与不变量

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 02

## Answer

**产出（10 个源码文件 + 92 个测试 + 词表 + 上下文级 ADR）**

| 文件 | 内容 |
|---|---|
| `NexusStackNext.Identity.Domain.csproj` | **零 PackageReference**（不变量 3） |
| `Ids/IdentityIds.cs` | UserId / RoleId / MenuId / MenuTreeId / ApiResourceId / RefreshTokenId |
| `Platform.cs` | `[Flags]`，**修掉参照仓库 `All = 0` 的语义冲突** |
| `ValueObjects/SecretHashes.cs` | `PasswordHash` / `TokenHash`——长度与空白校验，明文无处容身 |
| `ValueObjects/UserIdentityValues.cs` | `UserName` / `EmailAddress` / `PhoneNumber` |
| `ValueObjects/MenuPath.cs` | 物化路径（段序列）+ `RoutePattern`（权限键格式的唯一出处） |
| `Users/User.cs` | 注册 / 改密 / 启停 / 登录成功失败 / 锁定策略 |
| `Roles/Role.cs` | 授权集合 + **系统角色不可删除** + 只在集合真变时发事件 |
| `Menus/MenuTree.cs` | 整棵树一个聚合：增删改移、路径自维护、环检测、深度上限 |
| `ApiResources/ApiResource.cs` | 可授权端点 + 权限键 |
| `Tokens/RefreshToken.cs` | **只存哈希**、一次性消费、撤销幂等 |
| `Events/IdentityDomainEvents.cs` | 8 个领域事件 |
| `src/Services/Identity/CONTEXT.md` | 词表（含 `_Avoid_`） |
| `src/Services/Identity/docs/adr/0001-menu-tree-is-one-aggregate.md` | 上下文级 ADR |

**验收标准**

| 验收项 | 结果 |
|---|---|
| `Identity.Domain` 无任何 `PackageReference` | ✅ 且被架构测试白名单强制 |
| 每个聚合 ≥3 个不变量测试（含边界与反例） | ✅ User 13 / Role 11 / MenuTree 15 / RefreshToken 9 / 值对象 44 |
| 删除系统内置角色抛领域异常 | ✅ `EnsureDeletable_OnSystemRole_ThrowsDomainException` |
| Menu 物化路径只能通过领域方法变更 | ✅ `MenuNode.Path` 私有 setter，只有 `MenuTree` 能改写 |
| RefreshToken 持久化模型无明文字段 | ✅ 反射断言：只有 `TokenHash`，没有 `Token`/`PlainToken`/`RawToken` |
| 聚合可直接构造、无需全局状态 | ✅ `Register_NeedsNoGlobalState`（一次构造 100 个） |

**从服务层搬进领域的四条不变量（带参照仓库位置）**

| 原位置 | 新位置 | 原来为什么会被绕过 |
|---|---|---|
| `RoleService.cs:32-35` 的 `if (entity.IsSystem) throw` | `Role.EnsureDeletable()` | 换个调用方就能绕过；而且它拦的是"修改"，删除反而没拦 |
| `MenuService.cs:43/47` 手工拼 `IdSequences` | `MenuPath` + `MenuTree` | 调用方可以直接给字符串赋值，拼错了没人拦 |
| `MenuService.cs:79` `LIKE '%{parentId}%'` 找子节点 | `MenuPath.IsAncestorOf`（按段比较） | **两个独立缺陷**：父节点 `1` 会命中 `12`、`21`（结果错）；前导通配符让索引失效（性能差） |
| `PlatformType.cs:16` 的 `All = 0` | `Platform.None` 与 `Platform.All` 分开 | 同一个值在一处当"全部"、一处当"无"，权限判定会静默失效 |

**顺带修掉的两个安全问题（在领域层面根治）**

- 刷新令牌：参照仓库明文入库且"校验后再作废"跨三个 `await` 无并发保护，同一条能被用两次。
  新模型里**根本没有明文字段**（反射测试强制），一次性语义由 `ConsumedAt` 承载。
- 用户密码：参照仓库的哈希逻辑散在 `UserService` 里。现在只有 `User.ChangePassword` 能改，
  且内置账号**不可能被禁用**。

**一处需要说明的设计取舍**

票据写的是"不能禁用/删除**最后一个** Root"。我实现的是"内置账号**一律**不能禁用"——
这比"最后一个"更强，也更容易证明：判断"是不是最后一个"需要查库（跨聚合），
而"内置账号不可禁用"是聚合自己的不变量。副作用是没有"多个 Root 互相兜底"的能力，
如果将来需要，那是一条需要仓储参与的领域策略，应当单独立票而不是塞进实体。

**被架构测试抓出来的规格缺陷（重要）**

`DomainAssemblies_MustNotReferenceAnythingOutsideTheAllowList` 在 `Identity.Domain` 出现的那一刻**变红**：
白名单只允许 BCL + 自己，但上下文领域**必须**能引用共享内核 `BuildingBlocks.Domain`
（`AggregateRoot` / `ValueObject` / `Result` / `IDomainEvent` 都在那里）。

这不是误报——它是我在票据 05 写白名单时规格不完整，而在第一个真实上下文出现时被立刻暴露。
修法是给白名单加上唯一一个例外 `NexusStackNext.BuildingBlocks.Domain`，并注明
**`BuildingBlocks.Application` 与 `BuildingBlocks.Infrastructure` 不在白名单里**——
领域层依赖它们就是分层违规。

**反向验证（代码变异）**

去掉 `MenuTree.Move` 里的环检测条件 `node.Path.IsAncestorOf(newParent.Path)`：

```
失败! - 失败: 1，通过: 91    ← 只有 Move_IntoOwnSubtree_IsRejected_BecauseItWouldCreateACycle 变红
```

精确命中。检测条件已还原。

**当前状态**：构建 0 警告 0 错误；测试 **186/186**（领域 28 + 应用 21 + 基础设施 37 + Identity 领域 92 + 架构 8）。
