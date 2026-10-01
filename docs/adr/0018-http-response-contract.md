# ADR-0018：业务 JSON 使用统一响应契约

日期：2026-10-02

状态：已接受（[票据 #24](https://github.com/yongpengW/NexusStackNext/issues/24)）。

## 背景

NS 的 `RequestAsyncResultFilter` 提供统一成功、错误和分页字段。NSN 原先直接返回各模块的匿名 JSON 与 ProblemDetails，客户端需要分别适配，Identity 还丢失了业务错误码。

## 决定

HTTP 契约由 `BuildingBlocks.Web` 提供。Identity、Platform 等五个模块与网关共同使用，满足至少两个真实消费者后才提取的规则。它仅依赖 ASP.NET Core，不引用领域或存储程序集；领域的 `Result<T>` 仍只表达业务结果。

端点注入 `ApiResponses`，使用 `Ok(data)`、`Created(location, data)`、`Accepted(data)`、`Page(items, total, request)`。宿主显式调用 `AddApiResponseContract()` 与 `UseApiResponseContract()`，并保留 `UseExceptionHandler()`。不通过扫描、反射或缓冲响应流自动改写任意结果。

错误经 `IProblemDetailsWriter` 写成 `ApiProblemDetails`。各模块保留自己的错误码到 HTTP 状态映射；统一的是结构。JSON API 的错误响应固定为 `application/problem+json`，包括请求的 Accept 不含 JSON 时。框架绑定错误保留其 400/415 等状态；未知异常使用 500，隐藏内部异常说明。5xx 详细诊断走服务端日志。

网关只构造自己的 JSON 结果与空错误响应；有正文的下游响应直接透传。`traceId` 使用当前 W3C Activity 的 TraceId，并写入 `X-TraceId`；无 Activity 时回退至请求标识。既有 `X-Correlation-Id` 仍用于调用方关联，不能用它替代追踪标识。

## 线协议

成功（200、201、202）：

```json
{"success":true,"code":200,"message":"Success","data":{"value":null},"timestamp":1790899200000,"traceId":"..."}
```

失败保留 ProblemDetails 字段，增加统一字段；`code` 是数字 HTTP 状态，`errorCode` 是稳定的字符串业务码（无业务码时 `http.400` 等）：

```json
{"type":"about:blank","title":"用户名已存在","status":409,"instance":"/api/identity/users","success":false,"code":409,"message":"用户名已存在","data":null,"timestamp":1790899200000,"traceId":"...","errorCode":"identity.user_name.taken"}
```

分页在同一层增加 `total`、`page`、`limit`、`totalPage`，`data` 是本页数组。`page` 从 1 开始，默认 1；`limit` 默认 50，范围 1–200。空集合 `totalPage=0`，超出末页返回空数组，非法参数返回 400。

Platform 配置按 Key 的 ordinal 顺序分页，Scheduling 任务按 ID 升序分页。当前两个内存适配器先读取快照再分页；未来持久化列表应在所属上下文的查询端口下推排序、计数和分页，不在共享 Web 库查询数据。菜单树保留完整的 `data.count/items`，不截断父子关系。

文件下载、流、204、HEAD、OpenAPI 文档、健康探针及 SignalR 保留各自协议。201 的 Location、401 的 WWW-Authenticate、429 的 Retry-After 不变。新增特殊协议端点直接返回框架结果即可，无须额外跳过标记。

混合成功与失败的端点显式声明 `Produces<ApiResponse<T>>` / `Produces<ApiPage<T>>`，错误用 `ProducesApiErrors(...)` 声明。只返回 typed result 的端点由框架推导元数据。OpenAPI HTTP 测试同时校验外层字段与 data 的具体 schema，不能只检查路径存在。

## 迁移与验证

这是有意的响应契约变更：原先直接读 `accessToken` / `userId` 的消费者改读 `data.accessToken` / `data.userId`。错误消费者从字符串 `code` 改读 `errorCode`；`code` 与真实 HTTP 状态一致。分页消费者使用顶层分页字段与 `data` 数组。

仓库内 HTTP 测试与两条手动旅程脚本已迁移。真实 Kestrel 平台宿主与网关测试覆盖追踪、文件字节往返、下游业务错误透传、网关故障与限流。宿主 HTTP 测试覆盖成功、绑定失败、分页、201/202/204 和 OpenAPI。

本轮不加入自动操作日志；那需要另行定义敏感字段脱敏、请求大小限制与异步审计边界。
