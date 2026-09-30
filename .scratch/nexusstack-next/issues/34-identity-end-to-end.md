# 34 — Identity 端到端跑通（不需要 PostgreSQL）

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 33

> **这一票交付的是用户定义的"第一轮完成"：骨架 + Identity 端到端。**
> 而且它**不需要测试库**——端口存在的意义就是让内存适配器顶上。
>
> 十九轮前我判断这件事"阻塞于 PostgreSQL"。那个判断是错的。

## 实跑结果

```
健康检查                   -> HTTP 200
创建用户                   -> HTTP 201   {"userId":98328201679753216}
创建角色                   -> HTTP 201   {"roleId":98328201943994368}
注册端点（菜单 10）         -> HTTP 201   {"permissionKey":"/api/platform/settings/{key}:GET"}
授予角色菜单 10             -> HTTP 204
给用户分配角色              -> HTTP 204
查权限键                   -> HTTP 200   {"keys":["/api/platform/settings/{key}:GET"]}
授权判定（被授予的端点）     -> HTTP 200   {"decision":"Allowed","grantedCount":1}
授权判定（同菜单另一个方法）  -> HTTP 200   {"decision":"Forbidden","grantedCount":1}
重复用户名                 -> HTTP 409
不存在的用户查权限          -> HTTP 404
```

## 产出

| 文件 | 内容 |
|---|---|
| `Identity.Api/Program.cs` | 七个端点：用户 / 角色 / 菜单授予 / 角色分配 / 端点注册 / 权限查询 / **授权判定** |
| `Identity.Application/IPasswordHasher.cs` | 口令哈希端口 |
| `Identity.Infrastructure/Pbkdf2PasswordHasher.cs` | PBKDF2-HMAC-SHA256 实现 |

## 口令哈希：为什么是 PBKDF2

它在 .NET 基础类库里（`Rfc2898DeriveBytes`），**因此不需要引入任何第三方包**——
而"为了存口令引入一个依赖"本身就是一个供应链决定。迭代次数取 OWASP 对
PBKDF2-HMAC-SHA256 的建议量级；**编码串自带算法与迭代次数**，将来提高参数时旧口令仍校验得动，
不需要"让所有人改密码"。比较用 `CryptographicOperations.FixedTimeEquals`。

**它不是密码学创新。** Argon2id 抗 GPU 更强，但需要第三方库。
这个实现是"在标准库范围内做出的合理选择"，不是"最优选择"——这条差异写在类型文档里，
免得有人以为那是深思熟虑后的最强方案。

## 判定核心第一次在真实请求里被调用

`POST /api/identity/authorize` 走的是**真实的** `AccessPolicy.Decide`（票据 22），
不是在端点上重写一遍规则。上一步查到的权限键集合直接喂给它：
被授予的端点 `Allowed`，**同一个菜单下的 `DELETE` `Forbidden`**。

## 一处诚实标注

认证形态尚未确定，因此 `isAuthenticated: true` 与 `isRoot: false` 是**占位值**。
判定本身是真实的、吃的是真实的权限集合；但"发出这个请求的人到底是谁"还没有被验证过。
这一点写在端点注释里，不藏在文档角落。

## 这一票推翻的判断

| 十九轮前的判断 | 实际 |
|---|---|
| Identity 端到端阻塞于 PostgreSQL | **不阻塞**——内存适配器让它现在就能跑通 |
| 缺测试库 → 07/08/09 全部待开工 | 只有**EF Core 持久化**那一层被阻塞 |

端口存在的意义就是让持久化实现可以替换。我把"实现还没写"读成了"整条路走不通"。

## 状态

构建 0 警告 0 错误；测试 **349/349**；解决方案 **26 个项目**。
