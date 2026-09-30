# 33 — 权限投影：把四段链路接起来

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 31

> "Identity 端到端"的链路是 `用户 → 角色 → 菜单 → 端点 → 权限键 → 判定`。
> 票据 31 补上了第一段（用户拥有角色）。本票补上**第二处断裂**，并把整条链路做成一个可测的模块。

## 第二个模型缺口

**`MenuNode` 与 `ApiResource` 之间没有任何连接。** 角色被授予的是**菜单**，
而鉴权需要的是**端点的权限键**。没有这条连接，"授予了一个菜单"就翻译不成"能访问哪些端点"。

修法：`ApiResource` 带上可选的 `MenuId`。一个端点不对应菜单是合法的（后台接口），
但它因此**不会被任何基于菜单的授权覆盖**——那是刻意的，有测试守着。

> 两个缺口（票据 31 的角色归属、本票的菜单↔端点）都是**只有把两端接起来才会暴露**的：
> 每个聚合自己的单元测试都过，因为它们从没试过把一个聚合的输出喂给另一个。

## 产出

| 文件 | 内容 |
|---|---|
| `Identity.Application/IdentityRepositories.cs` | 三个端口：用户 / 角色 / API 资源 |
| `Identity.Application/UserPermissionReader.cs` | **投影模块**：四段链路 + 四种空值情形 |
| `Identity.Infrastructure/InMemoryIdentityStores.cs` | 三个内存适配器 + 显式注册 |
| `ApiResource.cs`（修订） | 补上 `MenuId?` |
| `tests/Identity.Application.Tests/` | 11 条测试 |

## 深模块

`UserPermissionReader` 的接口**只有 `ReadAsync` 一个方法**。它后面是四段链路、
四种"合法的没有权限"、以及一条错误路径。这是它深的地方。

## 四级"没有权限"是空集合，不是失败

没有角色 / 角色没被授予菜单 / 菜单背后没有端点 / 账号被禁用——
四者都返回空集合，由 `AccessPolicy` 按 fail-closed 拒绝（ADR-0010）。
**只有"用户根本不存在"是失败**：那是错误，不是"没有权限"。
两者必须能区分，否则排查时分不清"他没权限"和"他这个人不存在"。

## RBAC 核心第一次有真实输入

`EndToEnd_ProjectionFeedsTheAuthorizationDecision` 把投影结果喂给 `AccessPolicy.Decide`：
被授予的端点放行，**同一个菜单下的另一个 HTTP 方法拒绝**。
票据 22 的判定核心在此之前只有单元测试，**没有调用方**。

## 内存适配器不是占位符

它们是**真的适配器**：端口存在的意义就是让持久化实现可替换。
内存版让整个上下文能在没有数据库的情况下端到端跑起来，
也让缝的形状被第二个实现检验过（review/05 的判据）。
EF Core 版接入时应当**新增**而不是替换——测试仍然需要内存版。

## 一处自我纠正

测试的 `Fixture` 最初用 `FindAsync(...).GetAwaiter().GetResult()` 同步分配角色。
**而我在评审 04 F22 里把参照仓库的"构造体内 sync-over-async"列为可测试性问题之一。**
改成直接持有创建过的聚合实例，并把这个理由写进注释。

## 反向验证

去掉"被禁用账号一律没有权限"这条守卫后 **1 条测试变红**，精确命中。改动已还原。

## 下一步

`Identity.Api` 端点接线 + 运行时端到端验证。**这条路径不需要 PostgreSQL。**
