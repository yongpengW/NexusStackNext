# 09 — Identity API：端点与正确的 HTTP 语义

Status: resolved
Type: task
Labels: needs-triage
Blocked by: 08

## 要做什么

`src/Services/Identity/NexusStackNext.Identity.Api`：

- 用户 / 角色 / 权限 / 菜单的 HTTP 端点 + OpenAPI 文档。
- **正确的状态码与 `ProblemDetails`**——这是对原项目最直接的修正：原项目 `RequestJsonResult` 从不设置状态码，
  导致异常与 401 **全都是 HTTP 200**，SDK 与网关无法据此判断。
- 分页只采用一种明确约定（游标或 offset），不混用；不出现控制器里直接跨表 join
  （原项目 `OperationLogController.cs:57,74`）。
- 请求校验统一走票据 03 的验证管线。
- 显式组装：`Program.cs` 必须能一眼看出这个服务由什么组成（不变量 8）。

## 验收标准

- [x] 未认证请求返回 **401**（而非 200 + body.code=401）——在**边缘**验的（`GatewayAuthTests`）：受保护路由 401、公开路由不被挡、边缘自己的端点不设门。
- [x] 校验失败返回 **400** + `ProblemDetails`（`IdentityApiTests`：用户名太短、**空口令**、**非法标识**）。
- [x] 领域异常映射为 **409/422**，且响应体不包含堆栈——用户名重复 → 409（有测试）；未知用户 → 404；响应体是 `ProblemDetails`，只带 `title`，不带堆栈。
- [x] OpenAPI 文档可访问，且注释来自 XML 文档——`/openapi/v1.json` 返回身份模块的路径**且带 description**（只断言"路径存在"是不够的：一个只有路径没有说明的文档价值接近于零）。
- [x] 端点不直接引用 `DbContext`——新增 `EndpointPurityTests`（两层：编译产物 + csproj），**反向验证**：给 Identity.Endpoints 加一个 EF 引用 → 精确报出。

## 证据

- `review/03`：`RequestJsonResult` 不设状态码 ⇒ 异常/401 全 HTTP 200；`ApiAsyncExceptionFilter.cs:128-143` 内部还会再 throw。
- `review/02`：控制器直接跨表 join + 同步分页。
- `review/01`：`ExceptionHandlerMiddleware.cs:22-41` 对非鉴权异常只写日志不写响应体。

## 本轮完成

### HTTP 语义有了测试（`IdentityApiTests`，5 条 + `GatewayAuthTests` 3 条）

参照仓库最直接的缺陷是 `RequestJsonResult` **从不设置状态码**——异常与 401 全是 HTTP 200。
所以这些测试断言的是**状态码本身**，而不是响应体里的某个字段。

### 端点的"纯净"有了断言（`EndpointPurityTests`）

两层查：编译产物（真的用了）+ `csproj`（埋着可以用）——与
`ApplicationAssemblies_MustNotReferenceInfrastructure` 同一形状。
**反向验证**：给 `Identity.Endpoints` 加一个 EF 引用 → 精确报出文件名与片段。

### 校验管线接上了，而且它挡的是一件**领域挡不住的事**

域里的 `PasswordHash` 只校验"看起来像不像哈希"——它**从来看不到明文**。
于是**空口令会被哈希、被接受、被存起来**，整条链路上没有任何一环觉得不对。

分界线是**形状 vs 不变量**：口令策略（长度）是输入的形状，归校验器；
"口令哈希必须像哈希"是领域不变量，归聚合。

**反向验证暴露了更重要的一件事**：把校验器整体关掉之后，空口令返回的不是 201 而是
**500**——因为 `Pbkdf2PasswordHasher.Hash` 有 `ThrowIfNullOrWhiteSpace`。
校验器的价值不只是"给出一句更好的错误"，而是**把一个 500 变成 400**。

### 顺着它查出的第二个 500（已修）

同一个形状：`new UserId(0)` 会抛 `ArgumentException`，而标识常常**直接来自 URL**——
`GET /api/identity/users/0/permissions` 于是返回 **500**。用户输入不该让服务端抛异常。

修法是 `IdentifiedRequestValidator`：一个校验器覆盖所有带标识的请求。
**它一开始不生效**，因为 `Sender` 用 `GetService<IRequestValidator<TRequest>>()` 做**精确类型**查找，
而 **MS.DI 不按变体匹配**——`IRequestValidator<in TRequest>` 的文档写着
"可以为基类型注册一个校验器覆盖多个请求"，但那条能力**从来就没生效过**。

**又一条写在文档里、没人验证过的断言。** 改法是让查找显式走一遍请求实现的接口，
把那个说法变成事实。（这条测试修之前是红的，本身就是红-绿验证。）

### 两条按"没有可验证的对象"收口

- **分页约定**：本上下文没有列表端点，因此不存在"两种约定混用"这件事。
- **跨表 join**：`UserPermissionReader` 只用 Identity 自己的三个端口。

它们按事实收口，而不是假装做了。
