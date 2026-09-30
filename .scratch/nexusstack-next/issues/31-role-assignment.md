# 31 — 角色归属：Identity 纵向切片的第一刀

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 06

> **2026-09-29 说明**：本票的起因是我**推翻了自己上一轮的一个判断**。
> 我原以为"Identity 端到端阻塞于 PostgreSQL"——但**端口存在的意义就是让内存适配器顶上**，
> 而深模块评审（review/05）自己就写着"给最可疑的缝写第二个适配器"。
> 参考仓库真正的问题恰恰是**没有这道缝**：它和 EF 焊死了。
>
> 所以"缺测试库"不是端到端的阻塞项，只是**持久化实现**的阻塞项。

## 做切片时暴露的缺口

`User` 聚合**没有角色集合**——"这个用户有哪些角色"在模型里根本无法表达。
链路 `用户 → 角色 → 菜单 → 权限键` 的**第一段就是断的**。

这是只有把两段接起来才会暴露的问题：各聚合的单元测试都过，
因为它们从没试过把一个聚合的输出喂给另一个。

## Answer

**产出（4 个文件）**

| 文件 | 内容 |
|---|---|
| `Users/User.cs` | `RoleIds` + `AssignRole` + `RevokeRole` |
| `Events/IdentityDomainEvents.cs` | `UserRolesChanged` |
| `docs/adr/0002-user-owns-its-role-assignments.md` | 为什么角色归属住在用户聚合里 |
| `tests/Identity.Domain.Tests/UserRoleAssignmentTests.cs` | 8 条测试 |

**一个需要记录的设计决定**

"这个用户有哪些角色"由 `User` 聚合持有，不单独成 `UserRoleAssignment` 聚合。
理由：授权判定最不能容忍"刚授了权但还没生效"；分成两个聚合之后，
"用户存在但角色表里没有他"这种中间状态就成了**可表达的**——
而在权限系统里，可表达就等于迟早会出现。

**代价是真的，写在 ADR 里**：拥有大量角色的用户会让聚合变大、每次分配整聚合写。
触发条件也写了：**如果每个用户的角色数稳定超过几十，就重新评估这条决定。**

**空操作不发事件**

`AssignRole` / `RevokeRole` 在集合没有真的变化时是空操作**且不发事件**，与
`Role.Grant` / `Role.Revoke` 一致。参照仓库的缓存失效窗口最长有 10 小时，
无谓的失效事件会让它更难推理。

**反向验证**：去掉幂等守卫（重复分配也发事件）后 **1 条测试变红**，精确命中。改动已还原。

**当前状态**：构建 0 警告 0 错误；Identity 领域测试 **100/100**（原 92）；全量 **338/338**。

## 下一步（同一纵向切片）

1. `Identity.Application`：`CreateUser` / `CreateRole` / `GrantMenusToRole` / `AssignRoleToUser`
2. 内存适配器：`User` / `Role` / `MenuTree` 的仓储
3. `Identity.Api`：端点接线 + 一条调用 `AccessPolicy.Decide` 的授权判定端点，
   让 RBAC 核心（票据 22）第一次有**真实调用方**
4. 运行时端到端验证
