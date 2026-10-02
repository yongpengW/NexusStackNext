# NS / PoS 可复用能力与 NSN 补齐路线

核验日期：2026-10-02。本文是能力研究及实施建议，不是已完成验收清单。读取的是三个仓库的源码及测试源码；没有启动旧项目、读取配置值、访问生产数据库，也没有运行测试。NSN 的改动与测试执行由实施任务单独记录。

源码快照：NS `81831672aefc1f84db3ee5596a445df88ad26bdb`，PoS `6cb62274491ee50600d4103dc01a8a0a1bea114d`，NSN 在 PR #32 合并后的 `31975293e30523e3ac94736048e8e8d4a7105bb4`。研究过程中 NSN 已开始下一轮 Platform 改造，以下“当前缺口”均指这个已提交基线，不把同时进行的工作提前计作完成。

目标应当是保留 NS/PoS 的使用体验和可靠性需求，在上下文边界内补齐平台能力及代表性业务旅程。它不等于复制 ERP 的全部业务模块、业务公式或供应商实现。优先消除“宿主能跑、重启后数据却消失”和“有类型定义、真实业务链路却没接通”的差距。

## 1. 总览

| 能力 | NS / PoS 的实际价值 | NSN 基线结论 | 建议等级 |
|---|---|---|---|
| 统一 API 返回、分页、错误、TraceId | 前端有稳定公共协议，文件流可例外 | 已有真实 HTTP 契约测试；不能重新套一层 MVC Filter | 保留并随新端点回归 |
| 业务任务与 MQ | 接受后可查询、失败可重试、重复通知不重复计算 | Costing → Pricing 已有持久任务、事务 Outbox/Inbox、故障和跨进程测试；长任务进度和续租尚无公开能力 | 模板基础已建立；后续扩展长任务竖切 |
| Redis | 查询加速、令牌缓存、任务协调、管理入口 | Pricing 已有真实 Redis 协议与故障测试；不等于复刻了旧 `IRedisService` 每个命令，也不应如此验收 | 第二个真实缓存消费者再提取共享接口 |
| GlobalSetting | 持久化配置及按应用隔离；服务器读取通知等配置 | 内存；所有读匿名、写只要求登录；同值写/分组有测试，持久化及管理授权没有 | P0：持久化与受限读写 |
| Files / 下载中心 | 文件归属、自己的导出记录、失败状态、再次下载 | 字节落盘，元数据内存；上传 owner 为空；任意登录者可读/删已知 ID；无异步导出目录 | P0：归属、安全和重启恢复；随后导出闭环 |
| 审计 | 业务操作、异常和查询排错依据 | 实体审计字段有拦截器；独立审计只在内存，有匿名内部摄取入口；无生产事件订阅链和管理查询 | P0：持久审计与可信摄取 |
| Scheduling | Cron、启停、月初/周期任务、执行记录 | 固定 Interval、内存推进时刻；没有目标上下文任务登记链 | P1：持久到期触发闭环，再补日历计划 |
| 导入 / 导出 | 批量输入、行错误、后台生成文件 | Files 基础上传不能代替它们；当前无业务导入/导出旅程 | P1：Costing/Pricing 样板 |
| 实时通知 | 指定用户/业务范围的任务和业务变化提醒 | Gateway Hub 只推 clusterStatus；没有业务完成通知 | P1：任务完成 → 用户通知 → 断线补查 |
| 外部通知 / HTTP | Webhook、邮件等可追踪副作用 | 只有通用 HttpClient 韧性注册；没有通知执行与结果未知模型 | P1：本地受控接收端样板 |
| 身份与授权 | RBAC、撤权、用户启停、外部身份接入 | 本地登录/刷新/注销及权限计算已有测试；管理生命周期和非 Identity 上下文授权还需补 | P1：先补现有上下文管理权限，再按需企业身份接入 |
| OpenApp / Region 等平台元数据 | 外部应用配置和字典 | 仅在 CONTEXT 词汇中出现，不能视为已有端点/仓储 | P2：有真实消费者再竖切 |
| 多机高可用 | 多副本、故障域、恢复演练 | 用户明确最后实施；当前协议测试不能证明部署拓扑 HA | 最后单独票据 |

每行证据与可执行边界如下。P0/P1/P2 是建议顺序，不替代 GitHub Issues 状态。

## 2. 已有基础：API、MQ、任务与缓存

### 2.1 API 统一返回应保留语义，而非类名

NS 的 [`RequestAsyncResultFilter.OnResultExecutionAsync`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Filters/RequestAsyncResultFilter.cs) 包装对象、分页和错误，加入 TraceId，并允许跳过包装。PoS 使用同名 [Filter](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Filters/RequestAsyncResultFilter.cs)，成本端点既返回普通 DTO，也返回 `FileStreamResult` 或异步接受结果，见 [`CostManageController`](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/CostManageController.cs)。旧 Filter 把部分成功判断绑定为 `code == 200`，这种实现细节不应变成 NSN 必须兼容的缺陷。

NSN 的 [`ApiResponseContractTests`](../../tests/HostIntegration.Tests/ApiResponseContractTests.cs) 明确断言 201 的 Location、202/200 均成功、ProblemDetails、分页边界、415、框架错误、限流 Retry-After 与 OpenAPI；[`ApiTransportTests`](../../tests/HostIntegration.Tests/ApiTransportTests.cs) 覆盖传输边界。因此统一响应能力有实现与行为证据。后续 Files 流式下载、导入 202、业务错误必须继续经过这些公共契约，不能把二进制塞进逗号分隔的 JSON 字节数组。

### 2.2 MQ 的可靠性已经超越“有 Publisher 和 Handler”

NS [`AsyncTaskService.CreateTaskAsync/CreateDelayedTaskAsync/RetryAsync`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/AsyncTasks/AsyncTaskService.cs) 提供便捷登记和重试，但初次登记是先 Insert 再 Publish；[`EventSubscriber`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.RabbitMQ/EventSubscriber.cs) 有 ACK/NACK、Redis 去重和重投，发布器有固定延迟档位。PoS 已进化出 [`IAsyncTaskService` / `AsyncTaskService`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/AsyncTasks/AsyncTaskService.cs) 的执行代次、条件认领、进度更新、提交结果核对；这才是生产任务能力应参照的对象。

NSN 已有 [`PostgresTaskExecution`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Tasks/PostgresTaskExecution.cs)、上下文任务记录、Costing 的契约事件及 Pricing 消费，值得保留的行为证据包括：

- [`PricingWorkflowTests`](../../tests/Pricing.IntegrationTests/PricingWorkflowTests.cs)：并发同请求仅一个任务；改变请求内容被拒；两个 worker 只能一个持有未过期任务；旧代次不能完成/失败新任务；任务登记或完成出错回滚业务；重试预算有界且保留历史。
- [`CostIngestionTests`](../../tests/Pricing.IntegrationTests/CostIngestionTests.cs)：重复、旧事件、同消息 ID 改内容、登记失败回滚 Inbox，及业务数据来源所有权。
- [`BusinessCooperationTests`](../../tests/Costing.IntegrationTests/BusinessCooperationTests.cs)：真实网关、两个独立库、消费者被杀后的重投、broker 故障后的人工重驱。
- [`RabbitMqTopologyAndPublishTests`](../../tests/RabbitMq.IntegrationTests/RabbitMqTopologyAndPublishTests.cs) 与 [`RabbitMqConsumerRecoveryTests`](../../tests/RabbitMq.IntegrationTests/RabbitMqConsumerRecoveryTests.cs)：无法路由必须失败、拓扑幂等、连接被杀后恢复消费。

这不是“所有 NS RabbitMQ API 已等价”：业务任务的预定执行时间、任意长任务续租、分段进度、任务列表/筛选与取消语义还需业务竖切。已有任务记录的 `AvailableAt` 和失败重试不能自动证明已有面向调用者的延迟任务接口；已有 attempt history 也不等于已支持 batch checkpoint。见 [`DurableTaskRecord`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Tasks/DurableTaskRecord.cs)、[`CostingRequests`](../../src/Services/Costing/NexusStackNext.Costing.Application/CostingRequests.cs)。

外部核验：RabbitMQ 明确区分 publisher confirm 与 consumer ACK，两者不能互相证明业务已完成；连接故障可能导致重发和重复投递。因此继续以“本地事务保存 Inbox + 本地业务/任务后 ACK”为边界，不把 Redis 去重键或 publish 成功代替业务事实。[RabbitMQ confirms](https://www.rabbitmq.com/docs/confirms)、[RabbitMQ reliability](https://www.rabbitmq.com/docs/reliability)。

### 2.3 Redis 应按能力用途比较

NS [`IRedisService`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Redis/IRedisService.cs) 暴露 Get/Set、过期、NX/XX、Hash、Set 和 Scan。PoS 的接口再加入 [`LockAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Redis/IRedisService.cs)；真实消费者包括：

- [`WechatProgramsService.GetWeChatAppletAccessToken`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Wechat/WechatProgramsService.cs)：第三方短期令牌缓存；
- [`ScheduleTaskService.InitializeAsync/UpdateAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Schedules/ScheduleTaskService.cs)：计划定义读取缓存；
- [`CostBreakEvenPriceExecutionCoordinator.AcquireAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/CostManage/CostBreakEvenPriceExecutionCoordinator.cs)：可续期执行许可、锁丢失令牌；
- [`CostBreakEvenPriceRebuildTaskService.GetOrCreateAsync/GetOrRecoverAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/CostManage/CostBreakEvenPriceRebuildTaskService.cs)：任务创建去重与恢复。

NSN [`PricingRedisCache`](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingRedisCache.cs) 与 [`PricingCacheTests`](../../tests/Pricing.IntegrationTests/PricingCacheTests.cs) 覆盖跨进程共享、提交后失效、Redis 断线后重启重放、旧填充不能覆盖新结果、淘汰后回源、受限数据库降级。业务任务的执行权由 PostgreSQL 代次保护，优于把正确性寄托于 Redis 锁。

真实缺口是第二个生产语义消费者与治理，而不是 Redis 命令数量。建议随 Platform 元数据缓存或外部令牌适配器引入第二个消费者，再依据两者共性提取接口；不得把权限撤销、支付判断、任务所有权放进最终一致查询缓存。`UserPermissionCache` 当前为进程内缓存，文件本身明确多实例限制；该事实进入最后 HA 票据，不声称 PR #32 已解决跨实例撤权。参见 [`UserPermissionCache`](../../src/Services/Identity/NexusStackNext.Identity.Application/UserPermissionCache.cs) 和[缓存设计研究](2026-10-02-pricing-redis-cache.md)。

## 3. P0：平台设置的持久化与权限

NS [`GlobalSettingController`](D:/LeoProject/NexusStack/NexusStackBackend/Host/NexusStack.WebAPI/Controllers/GlobalSettingController.cs) 无匿名标记，继承认证基类；但 [`GlobalSettingService.GetSettingListAsync/GetSingleSettingForApiAsync/GetDefaltSettingAsync`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/SystemManagement/GlobalSettingService.cs) 实际返回 `null`，只能算模板占位。

PoS 则有真正的持久读取与操作：[GlobalSettingService](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/SystemManage/GlobalSettingService.cs) 按应用与 Key 定位配置，列表排除模板项；[GlobalSettingController](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/GlobalSettingController.cs) 所有读取与写入继承 [`ApiControllerBase` 的 Authorize](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/ApiControllerBase.cs)。它没有证明细粒度管理权限：[`RequestAuthorizeFilter`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Filters/RequestAuthorizeFilter.cs) 的旧菜单权限代码处于注释中，当前主要判登录和账号启用。

配置并非都适合公开。生产代码中的 [`SMTPEmailService.GetEmailConfigurationAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/EventAlert/SMTPEmailService.cs)、[`CommonSMSService.GetSmsConfigurationAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/EventAlert/CommonSMSService.cs)、[`WechatProgramsService.GetWeChatAppletSetting`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Wechat/WechatProgramsService.cs) 和 [`OperationLogArchiveService.GetLogArchiveConfigurationAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Archive/OperationLogArchiveService.cs) 都从 GlobalSetting 读取服务端配置。这里只核验使用模式，没有读取任何配置记录或值。

NSN 基线 [`PlatformModule`](../../src/Services/Platform/NexusStackNext.Platform.Endpoints/PlatformModule.cs) 将所有读取标为 `AllowAnonymous`，写入只要有效 JWT；[`SettingStore`](../../src/Services/Platform/NexusStackNext.Platform.Application/SettingStore.cs) 更新已有对象后没有显式仓储 Save，只因内存存着同一对象引用才生效。把字典替换成 DbSet 不足以完成迁移。[`SettingStoreTests`](../../tests/Platform.Application.Tests/SettingStoreTests.cs) 证明分组匹配、同值不发事件和清空保留条目，但使用内存，不能证明并发和重启。

建议竖切：平台 schema 的 PostgreSQL 存储 → 明确保存/事务接口 → 稳定键唯一约束和乐观并发 → 单独迁移命令、缺迁移启动失败与 readiness → 默认受限读写。公开设置应是显式白名单或单独 projection；秘密引用交给外部配置/secret 设施，不能建立一个匿名通用配置仓库。至少验证普通登录用户不能写、无权读取不能枚举、管理员授权变更立即影响本机下一次调用、重启保存、相同版本并发只接受一次、同值更新不增版本、回滚不产生外部事件。

## 4. P0：Files 必须先满足私有文件语义

NS [`DownloadController.GetListAsync/GetDownloadLinkAsync`](D:/LeoProject/NexusStack/NexusStackBackend/Host/NexusStack.WebAPI/Controllers/DownloadController.cs) 与 PoS [`DownloadController`](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/DownloadController.cs) 都限定 `CreatedBy == CurrentUser.UserId`，这是现有生产体验中的安全边界。NS 有本地与对象存储 [IFileStorage 实现](D:/LeoProject/NexusStack/NexusStackBackend/Infrastructure/FileStroage/IFileStorage.cs)，不能把存储厂商类型泄漏到新上下文业务接口。

NSN 基线的 [`FilesModule`](../../src/Services/Files/NexusStackNext.Files.Endpoints/FilesModule.cs) 上传复制整个请求到 `MemoryStream`、传 `ownerId: null`；下载/元数据/删除只要求登录。[`FileService`](../../src/Services/Files/NexusStackNext.Files.Application/FileService.cs) 不按 Owner 授权；[`InMemoryStoredFileRepository`](../../src/Services/Files/NexusStackNext.Files.Infrastructure/LocalDiskFileStore.cs) 重启丢元数据，磁盘留下不可寻址字节。领域文档虽写“知道标识不等于有权下载”，但产品代码尚未兑现。

现有 [`FileServiceTests`](../../tests/Files.Application.Tests/FileServiceTests.cs) 验证字节与元数据操作顺序、未知/软删/缺失内容、删除字节失败；[`FileStoreSeamTests`](../../tests/Files.Domain.Tests/FileStoreSeamTests.cs) 的第二适配器是测试内对象存储替身，不是生产 OSS/S3 实现。这些测试应保留，同时补真实 HTTP 与重启边界。

建议竖切：从已验证身份指定默认私有 Owner → 元数据持久化 → 按归属授权读/元数据/删除 → 请求大小与并发上限、流式/磁盘暂存 → 删除与孤儿清理可恢复。失败时不能回显磁盘路径、storage key 或原始异常文本。上传成功前必须确认字节写完；删除后元数据可隐藏，但清理失败要有持久待办和重试，不能让下一次删除因查不到元数据而永远无法清理。

验收：用户 A 上传，B 读/删失败；A 重启后可下载相同内容；超限、取消、写盘中断没有有效文件记录；元数据保存失败有可回收孤儿；删除中断重启后最终清理；边缘和直连宿主均一致。ASP.NET Core 官方要求服务器端限制大小、使用服务端生成存储名，并指出并发缓冲可能耗尽内存，支持大文件流式处理。[Microsoft 文件上传](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0)。

## 5. P0：审计记录与审计字段是两种能力

NS [`OperationLogActionFilter.OnActionExecutionAsync`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Filters/OperationLogActionFilter.cs) 在请求执行前发布日志，并序列化全部 ActionArguments；PoS 延续 [操作日志 Filter](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Filters/OperationLogActionFilter.cs)、[OperationLogEventHandler](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/EventHandler/OperationLogEventHandler.cs) 和管理查询。保留的是可追溯的操作，不是“记录全部请求体”，也不能把请求尝试当成业务成功。

NSN [`AuditInterceptor`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/AuditInterceptor.cs) 只负责实体 Created/Updated 字段；[`AuditTests`](../../tests/BuildingBlocks.Infrastructure.IntegrationTests/AuditTests.cs) 验证这些字段，不代表独立审计历史已落库。独立 [`AuditIngestion`](../../src/Services/Auditing/NexusStackNext.Auditing.Application/AuditIngestion.cs) 先登记 Inbox 再追加内存条目，异常时释放去重键；[`AuditIngestionTests`](../../tests/Auditing.Application.Tests/AuditIngestionTests.cs) 验证同进程去重及校验。它没有事务性生产存储、重启去重、同 MessageId 异内容冲突检查，也没有已接通的业务事件消费者。

[`AuditingModule`](../../src/Services/Auditing/NexusStackNext.Auditing.Endpoints/AuditingModule.cs) 的摄取 HTTP 为 `AllowAnonymous`，网关不路由；注释说明只是无 broker 验证入口。内部可达不应成为真实性证明。基线 PlatformHost 没有审计消息消费者，默认 [`NoIntegrationEventsMapper`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Events/NoIntegrationEventsMapper.cs) 也不会自动把所有领域事件变成审计。

建议竖切：选择一个已完成业务命令 → 提交同事务的最小审计契约 → 独立审计 Inbox 与不可变审计记录同事务 → 受权限保护的管理查询；生产关闭测试摄取入口或明确服务身份。查询是管理/调查用途，不要求业务上下文反向调用 Auditing，因而不必破坏“业务不依赖审计可用性”的边界，但需修订原“无查询端点”ADR。

验收：业务提交/回滚对应有/无事件；broker/审计库不可用不丢意图；重复和崩溃不重复记录；同 ID 异内容拒绝；actor、发生时间、接收时间、关联标识来自可信来源；密码/令牌/原始文件内容不进入审计；普通用户不能注入或读取其他人的审计。

## 6. P1：计划触发闭环与长任务进度

NS [`CronScheduleService`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Schedules/CronScheduleService.cs) 使用秒级 Cron、运行时表达式、Redis 单例许可和执行记录。PoS [`CostFirstDayOfMonthSchedule.ProcessAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Schedule/CostFirstDayOfMonthSchedule.cs) 实际登记后续异步任务；[`EmailNotificationDispatcherSchedule.ProcessAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Schedule/EmailNotificationDispatcherSchedule.cs) 扫描到期配置，逐个创建持久发送执行，单个失败不阻塞其余配置。这些是计划能力的实际价值。

NSN [`ScheduleRunner.RunOnceAsync`](../../src/Services/Scheduling/NexusStackNext.Scheduling.Application/ScheduleRunner.cs) 目前只 `ReadDue → MarkExecuted → Save`，[`InMemoryScheduledTaskStore`](../../src/Services/Scheduling/NexusStackNext.Scheduling.Infrastructure/InMemoryScheduledTaskStore.cs) 没有持久触发/Outbox。源码注释说一个失败不影响其他任务，但循环没有逐项错误处理；[`SchedulingTests`](../../tests/Scheduling.Application.Tests/SchedulingTests.cs) 也只验证到期推进、禁用和重复同刻无操作。不能把 `Triggered` 数量当成目标业务已经接受工作。

先做“到期一次 → 持久触发身份 → Outbox → Costing/Pricing Inbox 登记任务 → 可查询结果”。触发身份应由计划和计划发生时刻决定，数据库保证唯一；推进 NextRun 与触发意图同事务。随后增加 Cron/明确时区、DST、错过执行策略，保留 Interval 已声明的迟到不补跑行为，不能无声改变它。计划所属上下文只决定何时发生，执行结果归业务上下文。单机也应测试两个调度循环竞争与进程重启，这属于一致性正确性，不必等到多机 HA 才补。

长任务另取一个有界的成本批次样板：PoS [`CostBreakEvenPriceRebuildEventHandler`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/EventHandler/CostManage/CostBreakEvenPriceRebuildEventHandler.cs) 的 `EnsureCanContinueAsync/BeginSegment/ReportProgressAsync` 证明生产需要分段检查点、租约丢失取消、持久进度、内存许可。NSN 当前 `PostgresTaskExecution` 只有 Claim/Complete/Fail/Retry，尚无 Renew/Checkpoint。建议按执行代次更新检查点与单聚合结果同事务；每段释放 scope/内存；失去租约后旧执行者不能再落业务数据。验收应在段之间杀进程，重启从检查点继续，并拒绝旧代次迟到写入。不要把一个批次包装为跨聚合大事务。

## 7. P1：导出与导入用一条业务链证明好用

PoS [`CostManageController.ExportCostPriceAsync/ExportBreakEvenPriceSummaryAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/CostManageController.cs) 创建导出任务；[`AsyncTaskService.CreateExportExcelTaskAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/AsyncTasks/AsyncTaskService.cs) 关联下载项；[`ExportExcelEventHandler.HandleAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/EventHandler/ExportExcelEventHandler.cs) 生成内容、保存文件、标记下载状态。该处理器中的上传异步调用没有等待，不能复制“调用已开始就标 Success”的顺序。NS [`DownloadService.InitExportTypeMap`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/SystemManagement/DownloadService.cs) 的映射表为空，证明真正用法应参考 PoS，而非模板目录。

建议导出竖切：Pricing 受权限保护的查询快照/筛选 → 本上下文持久导出任务 → 流式生成无专有字段的 CSV/XLSX → Files 契约登记私有成果 → 查询任务与下载。业务查询和格式列定义留在 Pricing，Files 只管字节、元数据和归属，不能越界读 Pricing 表。验收包括任务接受后重启、结果只生成一个逻辑成果、文件不可用时不标成功、越权下载失败、数据量和执行资源有上限。首个格式的选型要基于许可和维护成本，不能因旧项目引用了某库就照搬。

PoS [`CostProductInfoService.ImportAsync/ValidateImportRows`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/CostManage/CostProductInfoService.cs) 保留输入行号、规范化输入、统计重复与处理数；外部批量通知 [`ReplenishNotificationBatchService.ReceiveAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/MasterPricing/ReplenishNotificationBatchService.cs) 用批次身份/内容摘要核对重复，持久记录各目标与后续执行。这些是通用批次能力，不需要移植具体成本规则。

建议导入竖切：提交有界 CSV/JSON 成本组成批次 → 先完整结构/行验证 → 明确全拒绝或逐行结果策略 → 本上下文任务分段落库 → 原有 Costing → Pricing 链。验收重复同请求返回原批次，同 ID 改内容冲突；错误含行号且不泄漏原敏感单元格；取消/重启/一行失败有明确定义；不采用隐藏的部分成功。NSN 基线没有这条链，普通文件上传不能算导入能力。

## 8. P1：业务通知与外部调用

NS [`NotificationRelayService.NotifyToUserAsync/NotifyToGroupAsync`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/SignalR/NotificationRelayService.cs) 把用户/组通知登记 AsyncTask。PoS [`SignalRNotificationService`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/SignalR/SignalRNotificationService.cs) 实际按用户、业务范围和设备推送；[`EmailNotificationExecutionService.CreateManualAsync/CreateScheduledAsync/TryClaimForSendAsync/CompleteSendAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/EmailNotifications/EmailNotificationExecutionService.cs) 保存发送执行与结果。NSN [`GatewayHub`](../../src/Gateway/NexusStackNext.Gateway/GatewayHub.cs) 仅有 `clusterStatus`；[`GatewayHubTests`](../../tests/HostIntegration.Tests/GatewayHubTests.cs) 只证明健康变化推送，不能证明任务完成通知。

建议实时竖切：一个任务完成事件 → 面向已认证用户的通知 projection → 网关只转发 → 客户端断线后按游标或任务查询补查。通知不是任务事实来源；重复通知允许去重，连接丢失不得丢掉最终业务结果。禁止客户端随意加入他人用户组/业务组。SignalR 官方明确组不是安全机制，撤权后需显式移除，重连的组成员关系也不能当持久授权。[Microsoft SignalR 用户与组](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups?view=aspnetcore-10.0)。

外部发送另做竖切，先用本地受控 HTTP 接收端，不能调用 PoS 真实服务或发送真实邮件。NS [`HttpClientService`](D:/LeoProject/NexusStack/NexusStackBackend/Infrastructure/Client/HttpClientService.cs) 提供异步请求封装；PoS [`HttpRequestClient`](D:/CWChina/CWChinaERP/CWChinaPoS/Infrastructure/Client/HttpRequestClient.cs) 有同步等待、异常转 null 的兼容形态，这些不应继承。业务层应有语义明确的调用端口，适配器处理认证引用、超时、有限响应、业务成功码和重试分类。

NSN [`ServiceDefaults`](../../aspire/NexusStackNext.ServiceDefaults/ServiceDefaults.cs) 注册标准 HTTP 韧性，但没有禁用不安全方法的自动重试。Microsoft 官方指出默认 handler 会重试所有方法，提供 `DisableForUnsafeHttpMethods`。因此将来接外部 POST 之前必须显式决定：无幂等协议的副作用不自动重放；支持幂等键的接收方可持久复用同一键，结果未知时先查询/核对。[Microsoft HTTP 韧性](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience)。

验收：超时发生在接收方已完成之后时，不产生第二次外部效果；429/Retry-After、永久业务拒绝、无效响应、取消各有行为；payload/凭据不进日志；外部故障不会占着数据库事务。邮件发送方不能保证收件系统 exactly-once，应明确发送结果未知和人工处理路径。

## 9. 身份、平台扩展与运维收口

NS [`RequestAuthorizeFilter`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Filters/RequestAuthorizeFilter.cs) 的预计算 API 权限集是保留点。PoS 成本端点另使用 [`OperationPermissionAuthorizationService.RequireAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Authorization/OperationPermissionAuthorizationService.cs) 做操作权限；企业身份用 [`PosPasswordAuthService`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/Authentication/PosPasswordAuthService.cs) 及 Authentik 用户映射/配置流程。后者是生产接入需求，不应把供应商专用登录脚本直接变成模板核心。

NSN [`IdentityModule`](../../src/Services/Identity/NexusStackNext.Identity.Endpoints/IdentityModule.cs)、[`IdentityUseCases`](../../src/Services/Identity/NexusStackNext.Identity.Application/IdentityUseCases.cs) 已有用户/角色/菜单/API 创建和绑定、登录/刷新/注销、权限查询。行为依据包括 [`TokenIssuanceTests`](../../tests/Identity.IntegrationTests/TokenIssuanceTests.cs) 的只存 refresh token hash、重放撤销链、禁用不能刷新，及 [`AuthorizationChainJourneyTests`](../../tests/HostIntegration.Tests/AuthorizationChainJourneyTests.cs) 的普通用户授权旅程。管理面目前不是 NS 全量 CRUD：修改、撤销角色/菜单授权、账号禁用/启用和凭据变更的公开旅程还需逐项盘点，不能由领域方法存在推导 HTTP 能力完成。

优先让 Platform/Files/Scheduling/任务运维使用明确操作权限，并补授权后可用、撤权后拒绝的真实 HTTP 验收。随后补齐 Identity 生命周期，再按明确环境做标准外部 OIDC 接入样板；不要求用户离开期间去配置真实企业身份提供方。模板默认本地身份仍可独立使用。

[`Platform/CONTEXT.md`](../../src/Services/Platform/CONTEXT.md) 提到了 `OpenAppConfig` 与 `Region`，但基线产品文件只实现 GlobalSetting。这些属于“文档中的候选概念”，不是已经完成的功能。OpenApp 可随受控 webhook 通知竖切落地；Region 随真实消费者需要再增加只读字典和初始化策略，不要提前建立万能元数据管理框架。

最后把运维能力做成可操作结果：各上下文任务列表与失败重试、Outbox/Inbox 积压年龄、审计查询、文件清理待办和依赖降级信息。已有 [`GatewayResilienceTests`](../../tests/HostIntegration.Tests/GatewayResilienceTests.cs) 证明单网关的限流和故障摘除，不等于多副本全局限流、共享路由管理、数据库/broker/对象存储灾备已完成。

## 10. 建议实施顺序和完成判据

1. **Platform 持久化和权限**：关闭默认匿名配置读取、管理授权、独立迁移、重启、并发、readiness。
2. **Files 私有存储闭环**：Owner、授权、元数据持久化、大小上限、可恢复清理、重启下载；之后再接对象存储的第二真实适配器。
3. **Auditing 可信持久摄取**：一条真实业务命令到审计记录、事务去重、受限查询和脱敏。
4. **Scheduling 持久触发到业务任务**：先 Interval 闭环，再 Cron/时区/漏跑策略和运维历史。
5. **长任务与批次**：续租、检查点、进度、重启恢复；随后导入/导出到自己的下载中心。
6. **通知与外部副作用**：任务完成实时提醒/补查，以及受控 webhook 的幂等、结果未知和重试样板。
7. **身份管理与元数据补齐**：对照实际调用补生命周期、OpenApp 与必要字典；第二个缓存消费者证明共享缝。
8. **跨模块完整旅程与模板生成验证**：从空数据库独立迁移、初始化管理员、授予普通用户权限、导入演示成本、异步计算、缓存查询、导出私有文件、查看审计和任务重试。通过真实网关执行，故障注入时验证仍可恢复。
9. **最后开多机 HA 票据**：故障域、负载均衡、数据库/broker/对象存储备份恢复、权限缓存跨实例撤销、网关共享管理状态、容量与 SLO 演练。不能以本轮功能全绿代替生产 HA 验收。

每轮都应先有公开接口的失败测试，再实现最小完整竖切，通过仓库固定的 build → 串行 tests → format 及相应检查，按 dev 基线做 Standards / Spec 双轴评审。完成状态只能来自实际行为和测试结果：存在一个 `Service`、`Repository`、契约类型、README 条目或绿色无匹配测试，均不能单独计为完成。
