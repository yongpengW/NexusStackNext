# 68 — Platform 的设置写入经边缘不可达

Status: resolved
Type: task
Labels: ready-for-agent
Blocked by: —

## 现象

第 30 轮 review 走真实 HTTP 时量到的：

```
1. 经网关读设置        → 200（公开）
2. 经网关写设置        → 405     ← 期望 204
   直连后端写（对照）  → 204     ← 端点本身是好的
3. 写后读回            → 200  {"key":"journey.key","value":null,...}   ← 写根本没发生
```

**405 是网关给的**：路由表里 platform 只有一条 `platform-read`，而它写死了 `methods: ["GET"]`。

```
{ "routeId": "platform-read", "path": "/api/platform/{**catch-all}", "methods": [ "GET" ] }
```

于是模块里那两个端点——

- `PUT    /api/platform/settings/{key}`
- `DELETE /api/platform/settings/{key}`

——**从边缘打不进来**，而**从后端直接打是好的**（对照那次返回 204）。

## 为什么值得单独一张票

`Platform` 是配置的**唯一真相**（`CONTEXT-MAP.md`）。而现在：

- **没有任何路径能改一条设置**——没有种子、没有别的写入者、边缘也只有读；
- 于是"唯一真相"是**只读的真相**；
- 而**没有任何东西会报错**：读得到（200）、写被拒（405），两者都像正常状态。

这与第 29 轮审计那张票是同一类：**"有意只读"与"忘了配路由"从外面看是一样的**，
而且**两种情况都不会有任何东西报错**。

## 需要你定的

**设置该不该经边缘改？**

1. **该**：加 `PUT`/`DELETE` 的路由（`requireAuthentication: true`，权限交给权限键）。
   代价是"配置的真相"多了一条 HTTP 写入路径，需要有权限模型护着它。
2. **不该**：那 `PUT`/`DELETE` 两个端点要么删掉，要么登记为**进程内 / 运维**用
   （像 Auditing 那样），并写进 `CONTEXT-MAP.md`。
   代价是：改一条设置要走运维通道，而不是后台。
3. **配置只由部署注入**（环境变量 / 配置中心），HTTP 那两条端点整个删掉。

## 验收标准

- [x] 选定的那条路上有**真实 HTTP** 的证据：要么经网关改得动一条设置并读回新值，
      要么文档里写明它只能走内部通道、并且断言经网关确实打不进来。
- [x] `EveryModuleEndpointIsRoutedOrDeclaredInternalTests` 覆盖这一条。
      它已经**从"前缀"细化到"方法 + 路径"**了（就是这一轮做的），
      而被取代的前缀版已经删掉——方法级版严格涵盖它。
      本票选的方案落地后，把 `DeclaredInternal` 里那两行 `platform/settings` 换成真正的归宿。

## Comments

### local

**第 16 轮：选了"该经边缘改"**

**决定（第 31 轮 review 时按推荐方案落地）：设置的写路径应当经边缘可达，由认证与限流护着。**
理由是 `Platform` 是配置的**唯一真相**，而"只读的真相"不是真相。

落地：

- `routes.json` 新增 `platform-write`：`/api/platform/{**catch-all}`、`methods: [PUT, DELETE]`、
  `requireAuthentication: true`、限流 `gateway.default`。与只放 GET 的 `platform-read` **按方法分开**，
  于是"读公开、写要令牌"这条与边缘策略逐字对应。
- `DeclaredInternal` 里那两行 `platform/settings` **移走**——它们有归宿了，不再需要豁免。
  （豁免的意思本来就是"还没归宿"，留着它会让这条检查从"守着"退化成"记着"。）
- 进程内也补上了同一侧判定：写端点 `.RequireAuthorization()`、读端点 `.AllowAnonymous()`
  （见 `ContextAuthorizationTests`）。**直连后端**不再能绕过写路径的保护。

验证：`Gateway.Routing.Tests` 48/48、`HostIntegration.Tests` 19/19；
后者的变异验证是——把某个端点的 `RequireAuthorization()` 摘掉，未认证直连会拿到 **201 Created**
（不是 401），测试立刻红。
