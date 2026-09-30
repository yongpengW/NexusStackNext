# 22 — RBAC 授权核心：权限键与判定

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 03

> **2026-09-29 说明**：这是从票据 08（Identity 应用层）与票据 11（授权）里切出的**可验证片段**——
> 两者都阻塞在持久化（19 → 15）与认证形态（10）上，但权限键与判定本身是**纯逻辑**，
> 不依赖数据库、broker 或认证实现。先做掉它，剩下的部分等外部输入。

## Comments

### local

**Answer**

**产出（3 个源码文件 + 32 个测试 + 1 份 ADR）**

| 文件 | 内容 |
|---|---|
| `BuildingBlocks.Domain/Authorization/PermissionKey.cs` | 权限键（值对象），**构造时归一化** |
| `BuildingBlocks.Application/Authorization/PermissionKeySet.cs` | 用户被授予的权限集合 |
| `BuildingBlocks.Application/Authorization/AccessPolicy.cs` | `AuthorizationMode` / `AccessDecision` / 判定 |
| `docs/adr/0010-authorization-defaults-to-deny.md` | 默认拒绝的决策记录 |

顺带把 `RoutePattern.PermissionKey(...)` 改为返回 `PermissionKey` 而不是裸字符串——
格式现在只有一处定义。

**两处针对参照仓库具体缺陷的设计**

1. **归一化收进类型，不再依赖"记得传比较器"。**
   参照仓库用 `HashSet<string>(keys, StringComparer.OrdinalIgnoreCase)`
   （`UserContextCacheService.cs:117`）——那是对的，但只要有一处忘了传比较器，
   就会退化成大小写敏感，表现是"**某些权限静默失效**"。
   现在 `PermissionKey.From(...)` 在构造时就归一化，普通集合即可，遗忘不再可能。

2. **默认拒绝，而且可穷举验证。** 见 ADR-0010。参照仓库的 fail-open
   （`RequestAuthorizeFilter.cs:87-111` + `ApiAuthorizationOptions.cs:21` 默认 `RootOnly`）
   意味着一配置事故就是全站放开。新版里每一个"不知道"都落到拒绝：
   枚举默认值、未声明的端点要求、未知的权限集、未知的枚举值。

**一个不显眼但会咬人的细节**

`PermissionKey.TryParse` **按最后一个 `:` 切分**。ASP.NET 的路由约束写作
`/api/items/{id:int}`，里面有冒号；按第一个冒号切会把 `{id` 当成路由、`int}:GET` 当成方法。
参照仓库从不解析（只比较字符串）所以没踩到，但一旦有人写缓存反序列化就会踩。
有一条测试专门盯这个。

**被编译器抓到的一处名字遮蔽**

`RoutePattern` 上的方法原本也叫 `PermissionKey`，与类型 `PermissionKey` 同名——
在成员内部，**成员名会遮蔽类型名**，于是 `PermissionKey.From(...)` 被解析成递归调用自己。
改名为 `ToPermissionKey` 后消失。这类错误编译器会报，但只有当你真的写出冲突时才会看见。

**反向验证（代码变异）**

把 `PermissionKey` 模式下"端点未声明要求"的结果从 `Forbidden` 改成 `Allowed`
——即参照仓库的 fail-open：

```
失败! - 失败: 1，通过: 52
  ✗ PermissionKey_WithNoDeclaredRequirement_Denies
```

精确命中。改动已还原。

**当前状态**：构建 0 警告 0 错误；测试 **279/279**。

**仍未完成的部分（留在原票）**

- 票据 08：把权限键集合从数据库投影出来（需要仓储，阻塞于 19）。
- 票据 11：把这个判定接到 HTTP 管线上（需要认证形态，阻塞于 10）。
