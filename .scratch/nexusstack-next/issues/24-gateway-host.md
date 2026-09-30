# 24 — 网关宿主：路由、关联 ID、fail-closed 认证

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: 23

> **2026-09-29 说明**：票据 23 交付了路由模型与校验，本票把它接到真实的 YARP 宿主上。
> 票据 12 剩下的部分是限流、路由持久化与管理 API 鉴权。

## Comments

### local

**Answer**

**产出（5 个文件 + 7 个映射测试）**

| 文件 | 内容 |
|---|---|
| `src/Gateway/NexusStackNext.Gateway/NexusStackNext.Gateway.csproj` | Web 宿主，YARP + 路由库 |
| `.../Program.cs` | 显式组装：载入校验路由表 → 映射 → 管线 |
| `.../GatewayAuthentication.cs` | **永远不认证**的默认方案（fail-closed 的落点） |
| `.../CorrelationIdMiddleware.cs` | 关联 ID：生成 / 沿用 / 转发 / 回传 |
| `.../routes.json` | 路由表（配置，不是代码） |
| `Routing/YarpConfigMapper.cs` | 路由表 → YARP 配置的**纯函数**映射 |

**运行时验证（把网关架在两个真实后端前面）**

```
网关 /health                    -> HTTP 200
网关 转发公开路由                -> HTTP 200          ← 真的在转发
  X-Gateway 转换                : nexusstack         ← 转换生效（参照仓库会丢）
  X-Correlation-Id              : 52ce74a4…          ← 网关生成
关联 ID 沿用（带 trace-abc-123） -> trace-abc-123       ← 沿用而不覆盖
受保护路由（未配置认证）          -> HTTP 401           ← fail-closed
/gateway/routes                 -> HTTP 200
```

**配置无效时拒绝启动，并说清原因**

```
Unhandled exception. System.InvalidOperationException:
路由配置不可用（…\bad-routes.json）：gateway.route_table.invalid
— 路由表有 1 个问题：gateway.route.unknown_cluster
```

参照仓库的网关在生产环境因配置缺失而直接起不来，错误信息是空连接串异常——
同样起不来，但没有人能从那条信息里知道该改什么。

**fail-closed 是怎么做到的**

不是靠自定义中间件，而是用框架自己的机制：受保护路由挂
`AuthorizationPolicy = "gateway.authenticated"`（策略内容为 `RequireAuthenticatedUser()`），
而宿主的默认认证方案是一个**永远返回 `NoResult`** 的处理器。
于是"忘了配置认证"的结果是**全都进不去**，不是"全都进得来"。

票据 10 定下认证形态后，真实处理器替换这个默认方案即可，路由表与策略名都不用改。

**"不丢字段"现在是被强制的**

`YarpConfigMapper` 暴露 `MappedRouteProperties` / `DeliberatelyUnmappedRouteProperties` 等清单，
一条**反射测试**断言"两个集合之并 == 模型的全部属性"。
也就是说：**给模型加字段却忘了映射，测试会红。**
参照仓库的丢字段不是有人故意删的，就是漏了——靠人记住是不够的。

**运行时抓到的一处配置与实现不匹配（值得记下来）**

第一次跑通时，转发返回 **500**。YARP 的日志说得很清楚：

> The timeout was not applied for route 'platform-read', ensure
> `IApplicationBuilder.UseRequestTimeouts()` is called between `UseRouting()` and `UseEndpoints()`.

路由级 `Timeout` 需要 ASP.NET Core 的 Request Timeouts 中间件配套。补上
`AddRequestTimeouts()` + `UseRequestTimeouts()` 后正常。

这一条的形态正是参照仓库的老毛病——**配置写了却不生效**。区别在于 YARP 选择在每个请求上
大声抛错，而参照仓库是静默忽略。所以这里**没有**把它当成"配置错了"，而是当成
"少接了一根线"：报错信息直接告诉你接哪根。

**当前状态**：构建 0 警告 0 错误；测试 **313/313**；解决方案 **22 个项目**。

**仍未完成（留在票据 12）**

- 限流、关联 ID 之外的边缘策略（重试/熔断由 YARP 的 HttpClient 配置承担，尚未启用）。
- 路由持久化：不放 `AppContext.BaseDirectory`（参照仓库每副本一份、非原子写）。
- 路由管理 API 及其**鉴权**（参照仓库那套 API 因认证方案未注册而必然 401）。
