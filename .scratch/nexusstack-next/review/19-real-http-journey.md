# 评审 19 —— 真实 HTTP 的端到端旅程（第 27 轮）

前九遍 review 都在看**代码与文档**。这一遍换成一个用户会问的问题：

> **从外面，一个人到底能不能登录进来？**

方法：起**两个真进程**（平台宿主 5191 + 网关 5190），用 `curl` 走**真实 HTTP**。

## 发现：**没有任何用户能登录**

第一次跑：

```
1. 经网关注册          → 401
2. 经网关登录          → 401
   直连后端登录        → 400（对照：端点活着，是我 payload 字段写错了）
```

网关日志：

```
Authorization failed. These requirements were not met:
DenyAnonymousAuthorizationRequirement: Requires an authenticated user.
```

### 根因

路由表里一条**前缀路由**：

```json
{ "routeId": "identity-management", "path": "/api/identity/{**catch-all}",
  "requireAuthentication": true }
```

它把 `POST /api/identity/login`、`POST /api/identity/refresh`、
`POST /api/identity/users` **全都吞了进去**。

**于是登录本身需要一个令牌。**

而"业务服务不对外暴露、边缘是唯一入口"是部署不变量（`AGENTS.md`）——
所以这不是"某条路少配一个开关"，是**系统对用户关闭**。

### 为什么所有测试都是绿的

现有集成测试用 `WebApplicationFactory` **直打平台宿主**，因此**跳过了网关的路由策略**。
`requireAuthentication` 是在边缘判定的，宿主里根本没有这个东西。

**这是这一轮最值得记的一句**：

> 集成测试直打宿主时，**你测的不是用户走的那条路**。

## 修法与验证

### 修

给三个必须匿名可达的端点各加一条**精确路径**路由（YARP 让更具体的路由胜出）：
`identity-login` / `identity-refresh` / `identity-self-register`。

### 验（同一段旅程，修完之后）

```
1. 经网关注册          → 201   ✅
2. 经网关登录          → 200   ✅ 拿到令牌
   直连后端登录        → 200   ✅（对照）
3. 带令牌经网关        → 403   ✅ 已认证但无该权限，fail-closed 正确
4. 不带令牌经网关      → 401   ✅
5. 经网关登出          → 204   ✅
6. 登出后同一个令牌    → 401   ✅ **撤销在真实 HTTP 上立即生效**
```

第 6 步值得单独看：**会话版本撤销**这件事此前只有进程内的测试守着，
现在在**两个真进程 + 真 JWT + 真网关**上验过了。

## 两条防线，一贵一贱

| | 跑得多快 | 验的是什么 |
|---|---|---|
| `AnonymousEndpointsAreReachableTests`（静态读 `routes.json`） | 每次构建 | "该匿名的端点在路由表里有没有匿名路由" |
| `scripts/verify-user-journey.ps1`（两个真进程 + curl） | 手动 | "用户到底走不走得通" |

**静的那条便宜但不是事实**——它比对路径，不跑 YARP 的匹配；**动的那条是事实但贵**。
两条都要，而这一点已经写进 `AGENTS.md` 的纪律一节（新增第四条）。

静态那条做了反向验证：拿掉 `identity-login` 那条路由 → 红，并点名
`POST /api/identity/login`。

## 附带：一条**把 bug 当成规格**的测试

修好路由表之后，`GatewayAuthTests.ProtectedRoute_WithoutCredentials_Returns401` 红了：

```
Expected: Unauthorized    Actual: BadGateway
```

**因为它挑的样本是 `POST /api/identity/users`——自注册，产品决定里就是匿名的。**
它原来绿，不是因为"边缘正确地挡住了写操作"，而是因为那条前缀路由把登录也挡住了。

> **一条断言"当前行为"的测试，会把 bug 固化成规格。**
> 而它固化的方式非常隐蔽：注释写得头头是道（"受保护路由（写操作）"），
> 只是**样本挑错了**。

已换成真正需要管理员权限的操作（给人分配角色），并**补了一条断言把产品决定固定住**：
自注册不经认证层被拦下。HostIntegration 从 16 条变成 17 条。

## 这一轮的账

| | |
|---|---|
| 修复的缺陷 | 网关路由表把匿名端点吞进受保护前缀（**用户登不进来**） |
| 新增的防线 | 静态路由检查 + 端到端旅程脚本 + 一条产品决定断言 |
| 顺带修正 | 一条把 bug 当规格的测试；`AGENTS.md` 新增第四条纪律 |

全量：**21 个工程、510 条测试、退出码 0**。
