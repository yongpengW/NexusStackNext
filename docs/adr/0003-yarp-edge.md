# YARP 作为独立边缘服务：认证在网关，授权在上下文

原项目的网关引用了包含全部业务与基础设施的 `NexusStack.Core`，路由依赖手工维护的 proxy 配置，
且路由管理 API（`GET/POST/PUT/DELETE /routes`）没有任何鉴权。

决定：`src/Gateway/` 保留 YARP 作为独立边缘服务，负责路由、**认证（验签）**、限流、关联 ID、汇聚 Swagger。
**授权（这个用户能不能做这件事）留在各上下文**。网关不得引用任何上下文的 `Domain` 或 `Infrastructure`。

## Considered Options

- **不要网关 / 只用 Aspire 开发期网关**：前端直连各服务，鉴权与限流会散到每个服务，版本收敛也无处安放。
- **把授权也放网关**：每新增一个上下文都要改网关，且网关必须理解各上下文的权限模型——耦合方向反了。

## Consequences

- 路由配置必须持久化并支持热更新；原项目 `JsonProxyConfigStore` 的并发处理值得沿用。
- 网关的路由管理 API 必须鉴权——这一条原项目缺失，是重做时必须补上的。
