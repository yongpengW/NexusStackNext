# 异常处理职责

NSN 的 HTTP 请求已经有统一异常出口。业务代码只在能够给出明确业务结果、清理资源或安排恢复时捕获异常；未知异常交给宿主处理。治理目标是减少重复处理与误报成功，不以 `catch` 数量作为完成标准。

## HTTP 与业务结果

- 预期业务拒绝使用 `Result` / `Result<T>`。各模块把自己的错误码映射到 HTTP 状态；不把任意异常转成业务成功、`null` 或空列表。
- 每个宿主显式组合 `AddApiResponseContract()` 与 `UseExceptionHandler()`。未知异常返回统一的 500 Problem 契约，保留追踪标识，隐藏内部错误详情；框架绑定错误保留 400 / 415 等真实状态。响应已开始后不能重新包装流。
- 调用方取消继续传播。内部执行预算耗尽可按所属模块协议返回 `unavailable` / `execution_timeout`，不能把两者合成同一种失败。
- 不在每个端点加重复兜底；特殊错误仅在拥有该语义的边界转换。共享 Web 契约见 [ADR-0018](adr/0018-http-response-contract.md)。

## 现有捕获的职责

| 边界 | 保留原因 | 当前实现 |
|---|---|---|
| Identity 命令事务 | 提交或回滚后转换明确的写冲突/容量拒绝；权限缓存只在提交后失效 | `IdentityCommandTransaction`、`IdentityCommandHandler` |
| Pricing / Auditing 导出持久化 | 内部预算、数据库暂不可用、跟踪状态清理；不推断提交是否成功 | `PricingExportExecution`、`PricingExportPublicationExecution`、`AuditExportProcessing` |
| 文件与导出字节 | 输入超限、摘要不匹配、磁盘不可用有明确结果；清理资源，保留原成果身份 | `FileService`、`GeneratedFileService`、导出 Producer |
| RabbitMQ / Webhook 适配器 | 反序列化拒绝、外部响应分类、确认与结果未知恢复 | `RabbitMqConsumer`、`HttpPricingWebhookSender` |
| Redis 适配器 | 缓存不可用时回源；已提交的失效待办仍须保留 | `PricingRedisCache`、`CostingRedisCache`、失效 worker |
| Worker / 计划逐项执行 | 一个执行失败不停止整轮或整个进程；持久化失败状态或在下一轮恢复 | `PricingWorker`、`SchedulingWorker`、`ScheduleRunner` |
| 操作观察与诊断 | 采集失败不能篡改已得到的业务结果；固定诊断，不递归采集 | `OperationObservationWriter`、`ObservedBackgroundExecution` |
| 事务、流与锁的资源边界 | 回滚、释放、清理后重新抛出，保留失败语义 | 所属事务适配器及 `finally` / `await using` |

这些职责不能全部交给 HTTP 中间件：后台执行没有 HTTP 请求，外部副作用也不能因抛异常就假定未发生。提交响应丢失时按稳定身份查询或恢复原意图，不能自动新建一份业务任务。

## 本轮治理与验证

[异常处理职责盘点与 Pricing 发布边界收敛 #190](https://github.com/yongpengW/NexusStackNext/issues/190) 将 Pricing 发布领取/完成重复的数据库捕获合并为类内一个执行方法。保留原有五秒预算、取消传播、错误码、事务与跟踪状态清理；不新增跨上下文异常框架、自动重试或配置。

验证沿已有公开接口：`PricingExportAcceptanceTests` 检查发布领取、回执校验、完成、幂等及人工恢复；`PricingExportCommitRecoveryTests` 检查服务端提交后丢失回复的原意图恢复。HTTP 统一契约由 `ApiTransportTests` / `ApiResponseContractTests` 验证。测试结果与完整 CI 资格记录在票据和 PR 中。

这份职责盘点与本轮收敛不代表后续新增代码无需评审。修改既有模块时，优先移除同一语义的重复捕获；仅因代码形状相似不提取通用包装器，也不为防止将来误用新增扫描护栏。
