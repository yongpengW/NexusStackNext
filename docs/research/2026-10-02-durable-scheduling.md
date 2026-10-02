# 持久计划触发到 Costing：一手资料与最小完整链路

核验日期：2026-10-02。对应 [票据 #43](https://github.com/yongpengW/NexusStackNext/issues/43)。仓库观察基于审计 PR42 的提交 `509a60c6bc966118dac0ee76467cb3fbbbed7aa1`；本文为研究与实现建议，尚未构成已接受 ADR，也不是并发、重启或故障验收报告。此次没有运行数据库测试，没有修改产品代码。

建议把本轮的闭环限定为：**持久计划定义 → 一次持久 Occurrence → Scheduling Outbox → Costing 的持久接受/拒绝结论与本地任务 → 现有 Costing → Pricing 链**。Scheduling 拥有何时触发，Costing 拥有输入和计算，消息发布成功、业务接受、业务完成分别可查。实现并发正确性不需要先部署多机，但必须现在验证两个扫描器竞争。

## 1. 仓库证据：已具备什么，缺口在哪里

以下是上述提交的源码观察，不是对新实现的描述。

| 位置 | 已有行为 | 本轮必须补齐的部分 |
|---|---|---|
| [ScheduledTask](../../src/Services/Scheduling/NexusStackNext.Scheduling.Domain/Tasks/ScheduledTask.cs) | 固定间隔；迟到后 `NextRunAt = at + Interval`；启停与 Version | 未绑定业务目标；`MarkExecuted` 实际只推进时间；间隔只校验正数，未限制时间范围和加法溢出 |
| [ScheduleRunner](../../src/Services/Scheduling/NexusStackNext.Scheduling.Application/ScheduleRunner.cs) | 每轮最多 50 条，逐项判到期、修改、保存 | 没有触发记录和 Outbox；没有逐项异常隔离，虽注释声称有；读后判断不能解决并发扫描 |
| [TaskRegistry](../../src/Services/Scheduling/NexusStackNext.Scheduling.Application/TaskRegistry.cs) | 定义、列出、启停；扫描全表做编码查重 | 先查再保存存在竞态；启停没有调用方预期版本；持久化后需要数据库唯一约束与条件写 |
| [InMemoryScheduledTaskStore](../../src/Services/Scheduling/NexusStackNext.Scheduling.Infrastructure/InMemoryScheduledTaskStore.cs) | 字典保存聚合引用；ScheduleRunner 为单例 | 返回的是共享可变对象，修改在 Save 前已可见；不能以它证明事务或并发；需保留等价的开发适配器并新增 PostgreSQL 适配器 |
| [SchedulingModule](../../src/Services/Scheduling/NexusStackNext.Scheduling.Endpoints/SchedulingModule.cs) | 管理组 RequireAuthorization；HTTP 包装和分页 | 缺少其他新模块使用的 NexusStackAuthorizationFilter 与逐端点权限；只认证不能证明 RBAC 和会话注销撤销 |
| [SchedulingWorker](../../src/Services/Scheduling/NexusStackNext.Scheduling.Endpoints/SchedulingWorker.cs) | PeriodicTimer 每 10 秒扫描，整轮异常后继续 | 直接持有单例 runner；引入 DbContext 后须改为创建作用域；原始异常日志须改为稳定故障码 |
| [Outbox 装配](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/InfrastructureServiceCollectionExtensions.cs)、[投递循环](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Events/OutboxDeliveryWorker.cs) | 一个未 keyed 的 OutboxPublisher 和一个 OutboxDeliveryWorker；默认未 keyed IOutboxStore | Platform 和 Scheduling 同宿主时不能再靠“最后注册的 store”；须把存储、策略和循环绑定到所属上下文 |
| [共享订阅循环](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Events/RabbitMq/RabbitMqSubscriptionWorker.cs) | 每条消息独立作用域；按 EventName 找 keyed processor；每订阅一个 IHostedService | 可直接作为 Costing 入站生命周期；仍需业务接收事务和查询结论 |
| [CostingCommands](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingServices.cs) | 请求 ID 幂等；按对象串行；更新输入与任务同事务 | UpdateCostInputs 是“更新输入”命令，不能让 Scheduling 携带成本快照冒充当前输入；增加 Costing 自己的计划接收入口 |
| [CostingExecution](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingExecution.cs) | 持久领取、代次、短事务完成、结果 Outbox | 可复用执行协议；需要证明计划登记的新任务能走完全链 |

[Scheduling ADR-0001](../../src/Services/Scheduling/docs/adr/0001-next-run-advances-in-the-aggregate.md) 中“调度器无状态，因此崩溃重启不会重复或漏触发”的推论不成立。当前代码既没有持久存储，也没有“推进时刻 + 保存触发意图”的原子提交，更没有接收方幂等。应由新 ADR 明确补全这些条件，并标记旧推论已被取代。

## 2. PostgreSQL：并发竞争和持久身份

**官方事实。** PostgreSQL 事务提供多个步骤整体提交/回滚；唯一约束在数据库内约束重复；`ON CONFLICT` 依靠对应唯一约束处理竞争。[事务](https://www.postgresql.org/docs/18/tutorial-transactions.html)、[唯一约束](https://www.postgresql.org/docs/18/ddl-constraints.html#DDL-CONSTRAINTS-UNIQUE-CONSTRAINTS)、[ON CONFLICT](https://www.postgresql.org/docs/18/sql-insert.html#SQL-ON-CONFLICT)

**官方事实。** `FOR UPDATE SKIP LOCKED` 可以用于队列式多消费者，跳过当前被锁住的行；它不是一致的全表视图，也不保证严格先来先服务。LIMIT 应有确定排序；跳过行锁也不代表完全没有表级等待。[SELECT 锁定子句](https://www.postgresql.org/docs/18/sql-select.html#SQL-FOR-UPDATE-SHARE)

**建议的接口。** 缝上变化的是“怎样原子登记一次触发”：PostgreSQL 用事务与约束，Memory 用锁和隔离快照。因此保留应用层端口，但把 `ReadDue → 任意修改 → Save` 深化为持久化能保证的原子操作，例如读取有界候选，再调用 `TryTriggerAsync(candidate, occurrenceId, triggeredAt)`。返回 Triggered / AlreadyRecorded / Conflict 或稳定失败，不暴露 DbContext 与 SQL。

每次原子触发只涉及一个计划聚合及其技术记录，事务内做：

1. 按计划 ID 锁定并重新读取，或以 Version 与预期 NextRunAt 条件更新；确认仍启用、仍到期、仍是扫描到的那一版。
2. 登记 Occurrence：稳定 ID、计划 ID、计划发生时刻 ScheduledAt、实际登记时刻 TriggeredAt、目标类型与对象 ID、可信委托人、用于关联的版本/序号。
3. 推进计划下一时刻并递增一次 Version，添加使用同一 Occurrence ID 的 Outbox。
4. 一次提交；任何一步失败都不留下部分结果。RabbitMQ 发布在事务外。

**建议的双重身份约束。** Occurrence ID 唯一；另有计划内逻辑发生身份的唯一约束，例如 `(PlanId, TriggerSequence)`，或明确禁止复用的计划版本与 ScheduledAt 组合。仅靠每次扫描 `Guid.NewGuid()` 不能拦住两个扫描器产生两项逻辑工作。优先使用计划内单调序号，避免暂停/恢复重设到同一 UTC 时刻、数据库精度及未来 Cron 重复墙上时间的歧义。若采用 `(PlanId, ScheduledAt)`，必须明确它在重新安排时仍唯一的业务约束。

**建议的竞争处理。** 对于本轮 50 条规模，“有界读候选 + 每个候选独立短事务 + 乐观条件”足够清楚；可在候选领取中使用 SKIP LOCKED 降低竞争，但不能把批量锁定的一个长事务同时跨过所有计划。启停提交必须携带 ExpectedVersion，旧管理请求不能覆盖扫描器已经推进的状态。EF 的并发令牌会把原版本放入更新条件，0 行则产生并发冲突；应用应将它转换为明确的冲突结果。[EF Core 并发](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)

**失败隔离。** 每项失败回滚后，继续当前批次的其他计划；下一项须使用干净 DbContext/跟踪状态。整轮读候选失败再交给外层退避。取消令牌应中止工作，不计为某计划的业务失败。长期位于最前面的坏计划还可能占满每一批，应至少记录失败计数/最近失败并考虑扫描游标或单项退避；“同批继续”本身不证明全队列公平。

## 3. EF Core retry 与提交结果未知

**官方事实。** 启用重试策略时，显式事务里的全部操作必须由 execution strategy 作为一个单元重放；仅重试 SaveChanges 不够。提交时断线可能已经提交，不能一概当回滚；官方提供稳定客户端身份及 `verifySucceeded` 等处理方式。[EF Core 连接恢复](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#execution-strategies-and-transactions)、[提交失败与幂等](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue)

**仓库证据。** [UseNexusStackPostgres](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/NexusStackDbContextOptionsExtensions.cs) 开启 EnableRetryOnFailure；Platform 使用这套配置。当前 [CostingDatabase.CreateContext](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingDbContext.cs) 直接 UseNpgsql，没有启用它。因此不能把 Costing 的裸 BeginTransaction 复制到 Scheduling 后认为同样可用，也不能无验证地给所有 Costing 命令统一开启重试。

**建议。** Scheduling 每项事务用 CreateExecutionStrategy 包住“开始事务 → 读取/核验 → Occurrence/Outbox/计划写入 → 提交”。候选身份、预期版本、Occurrence ID 和本次语义输入在可重放块之外固定。每次尝试重建上下文或清掉跟踪并重新查询；提交结果未知时，按稳定身份查询已登记的 Occurrence，并验证内容一致，不能重新生成身份、重新读一个到期计划又算一次。

仅把事务放进 ExecuteAsync 不等于解决未知提交：如果回调重新读取“现在到期的任何计划”，前一次已提交后重试可能处理下一次发生；如果缓存了已修改聚合，重试还可能重复推进 Version。应把重试绑定到同一个候选意图，并用持久记录确认结果。测试必须包括提交成功但调用方未收到成功的重放情形，不能只注入提交前异常。

## 4. 时间、固定间隔与 Cron 的边界

**官方事实。** PostgreSQL timestamptz 以 UTC 保存瞬间，精度为微秒，不保留原输入的时区名称；CURRENT_TIMESTAMP 固定在事务开始，clock_timestamp() 反映调用时刻。[时间类型](https://www.postgresql.org/docs/18/datatype-datetime.html)、[当前时间函数](https://www.postgresql.org/docs/18/functions-datetime.html#FUNCTIONS-DATETIME-CURRENT)

**本轮建议。** 固定间隔表示经过的时长；存 UTC，使用受界限保护的时长，并保持 `NextRunAt = TriggeredAt + Interval`。TriggeredAt 是触发意图被登记的时刻，不能命名为业务完成时刻。恢复时只登记一个到期 Occurrence，保留原 ScheduledAt 供观察延迟；不补造停机期间每个历史节拍。数据库适配器可在取得计划锁后取数据库时间，并把它传给聚合；Memory 使用注入时钟。时刻参与幂等或比较前须规范到存储精度，避免 .NET 100ns 与数据库微秒往返差异。

**官方事实。** PeriodicTimer 会合并两次等待之间积累的 tick，且同一时刻只允许一个等待者。[PeriodicTimer](https://learn.microsoft.com/en-us/dotnet/api/system.threading.periodictimer.waitfornexttickasync?view=net-10.0)

**推论。** Timer 只是唤醒扫描的工具，不能充当持久发生记录；tick 合并也不能证明业务层的漏跑策略。一个进程内串行等待不能替代数据库中的跨进程竞争控制。

**后续 Cron 边界。** Calendar Cron 将要求表达式、时区 ID、策略版本、漏跑策略及每次实际 UTC 发生身份；不能把固定秒数简单改名为 Cron。DST 回拨可能令同一当地时间映射到两个 UTC 瞬间；前拨可能产生根本不存在的当地时间。下一票要选择跳过/顺延及重复一次/两次的规则，并用 DST 两端的例子测试。固定 offset 不能代替时区规则。[Microsoft：歧义时间](https://learn.microsoft.com/en-us/dotnet/standard/datetime/resolve-ambiguous-times)、[无效时间](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.isinvalidtime?view=net-10.0)

## 5. 后台作用域与同宿主多个 Outbox

**官方事实。** Hosted service 默认没有 DI scope，使用 scoped 服务须显式创建作用域；AddHostedService 注册的是单例。[ASP.NET Core 10 后台服务](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0#consuming-a-scoped-service-in-a-background-task)、[BackgroundService 的 scoped 依赖](https://learn.microsoft.com/en-us/dotnet/core/extensions/scoped-service)

**官方源码事实。** .NET 10 的 AddHostedService 两个重载均调用 TryAddEnumerable；相同实现类型不会因此变成多个独立后台循环。[dotnet/runtime v10.0.0 注册源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/ServiceCollectionHostedServiceExtensions.cs)

**建议。** SchedulingWorker 注入 IServiceScopeFactory，每轮创建 async scope，应用服务再在每项事务失败后保持清洁状态。不要把 scoped DbContext 经单例 runner 持有到整个进程生命周期。后台服务启动仍应遵守现有有界数据库检查与独立迁移模式。

Platform 和 Scheduling 已是两个真实生产者，此时抽出带上下文绑定的投递装配符合“不早于第二个消费者”的规则。建议每个 producer 明确传入存储工厂/键、OutboxDeliveryOptions 和日志上下文；可使用 keyed OutboxPublisher 加独立 IHostedService 工厂，或泛型上下文绑定。关键是每个循环只解析自己的 store，不能让它枚举所有服务再猜归属；未 keyed 旧入口若保留兼容，不能额外启动一个重复发布循环。

现有 [RabbitMqSubscriptionWorker](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Events/RabbitMq/RabbitMqSubscriptionWorker.cs) 已用 `AddSingleton<IHostedService>(factory)` 保留每个订阅，可作为多实例注册的实现参照。验收必须在同一真实 PlatformHost 中同时写一条 Platform 设置事实和一条 Scheduling 发生，两条都跨 RabbitMQ 到达各自接收方；只数 DI 注册个数或分别测两种宿主不足以证明正确装配。

## 6. RabbitMQ：发送、接受和完成的责任边界

**官方事实。** Publisher confirms 覆盖发布者与 broker，consumer acknowledgements 覆盖 broker 与消费者；两者互相独立，前者不知道业务消费者是否完成。[RabbitMQ confirms](https://www.rabbitmq.com/docs/confirms#are-publisher-confirms-related-to-consumer-delivery-acknowledgements)

**官方事实。** Confirm 丢失后重发可能重复；消费者必须可去重或幂等；消费者应在自己的处理责任已可靠完成后才 ACK。持久队列与持久消息须配合，mandatory 用于发现没有任何适合队列的路由。[RabbitMQ 可靠性](https://www.rabbitmq.com/docs/reliability)

**仓库证据。** [RabbitMqEventBus](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Events/RabbitMq/RabbitMqEventBus.cs) 已有 persistent、mandatory 和 confirm 跟踪；[PricingCostIngestion](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingCostIngestion.cs) 已示范 Inbox、内容指纹及本地任务同事务登记。复用这些模块，不再做“先 ACK，再登记任务”的新路径。

建议保持三组独立可观察事实：

| 事实 | 所有者 | 对调用方说明 |
|---|---|---|
| Pending / Delivered / DeadLettered | Scheduling 的 Outbox/触发历史 | Delivered 只证明 broker 接管；不能显示“成本任务成功” |
| Accepted / Rejected，加稳定原因、TaskId | Costing 的计划接收记录 | Accepted 表示本地工作已持久登记；Rejected 表示业务拒绝已持久记录 |
| Pending / Running / Succeeded / Superseded / Failed 及尝试历史 | Costing 的持久任务 | 这是计算进度和执行结论；Pricing 再拥有自己的派生结果 |

本轮可以提供各自的受权 HTTP 查询，不必引入跨库查询或额外结果回传。Scheduling 查询只展示自己的交付事实；需要聚合视图时由调用层读取各上下文 API，或下一轮增加契约投影。

## 7. Costing 接受协议与最小契约

**建议契约。** 新的 Scheduling.Contracts 只传 OccurrenceId、PlanId、TaskCode、Target=`costing.recalculate`、ItemId、ScheduledAt、TriggeredAt、可信委托人和必要关联标识。字段有界，EventId 与 envelope.MessageId 一致；不接受任意 URL、程序集类型名、表达式代码、成本金额快照或用户令牌。

Costing 入站处理应在自己的数据库完成以下一个短事务：

1. 校验版本和字段，按消费者、事件名、OccurrenceId 尝试写 Inbox；对规范化最小内容计算指纹。
2. 若已有同身份，指纹相同返回原接受/拒绝结果；不同内容拒绝，不能修改原记录。
3. 使用与现有成本写命令相同的对象锁命名空间读取当前 CostSheet；同时保护计划任务身份，避免与人工请求复用同一个 TaskId。
4. 对象存在：复制此刻的已提交输入与 InputRevision 到本地持久任务，登记 Accepted 与 TaskId；无需把成本输入改写一遍。
5. 对象不存在：登记 Rejected 与稳定 `costing.not_found` 原因，不创建伪任务。Inbox 与拒绝结论仍一起提交，然后 ACK，避免永远重试一个已作结论的业务拒绝。
6. 数据库暂不可用：不 ACK，回滚整项，恢复后再消费；坏契约/同 ID 改内容走明确拒绝或死信，并保持原接受记录不可变。

拒绝结论必须可查。不能仅 return true 丢掉不存在的对象，也不能仅 return false 把每次重投都当新判断。相同 Occurrence 的内容已固定；在对象后来建立后是否重试业务拒绝是新业务决定，本轮不通过修改原消息实现，操作者可定义/等待新的发生。

**现有业务幂等边界。** [CostSheet.ApplyCalculation](../../src/Services/Costing/NexusStackNext.Costing.Domain/CostSheet.cs) 对相同 revision、相同结果是空操作；[CostingExecution](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingExecution.cs) 仍可用新任务 ID 发结果；[PriceQuote.ApplyCostingCost](../../src/Services/Pricing/NexusStackNext.Pricing.Domain/PriceQuote.cs) 会忽略同 revision、同金额。所以同一输入的计划重算完成后，Pricing 不一定产生新的工作或版本。这是保留业务幂等的合理结果，不能为了让 E2E 看起来“每次都有变化”而伪造 InputRevision。验收应覆盖当前结果一致及新输入真实变化两种情况。

## 8. 管理、恢复与验证切片

**仓库要求与建议。** 计划管理代表预先授权的后台执行委托。定义/启停/查看历史/重新交付均需明确权限及当前会话检查；委托人来自已验证身份。暂停只阻止未来发生，已提交的 Occurrence 仍会交付；注销立即撤销管理 API 会话，但是否撤销既有委托是不同业务问题，本轮保留已接受委托并明确文档。此语义来自 [票据 #43](https://github.com/yongpengW/NexusStackNext/issues/43) 的后台委托约束，应由 ADR 固定。

人工投递重试只允许死信状态，携带看到的失败代次/时间做条件写，复用原 Occurrence ID，不改变最小载荷。业务任务失败的重试则仍由 Costing 权限和 Epoch 控制，两个入口不要合并。

建议按以下公开边界逐条 red → green；这些是待验收场景，并非已执行结果：

| 边界 | 必须看见的行为 |
|---|---|
| Domain / 应用 | 迟到一小时只登记一次；NextRunAt 从实际触发起算；启停空操作版本不变；无效时长/溢出/非法目标拒绝；当前批次一项失败后其他项仍前进 |
| 隔离 PostgreSQL + 两个应用 scope | 两个扫描器同一候选只有一条 Occurrence/Outbox；计划版本只前进一次；约束注入失败后三项均回滚；唯一编码并发只能创建一个 |
| 提交恢复 | 固定意图被重放后返回已保存发生；计划/发生/Outbox 不重复；提交前崩溃无半成品，提交后发布前崩溃恢复后可投递 |
| Costing 应用/真实消息 | 并发重复一项本地工作；同 ID 改内容拒绝；缺失对象稳定拒绝可查；Inbox 成功而任务插入失败时全部回滚；接受后到 ACK 前退出仍不重复 |
| 真 PlatformHost + RabbitMQ | Platform 审计和 Scheduling 触发同时独立前进；broker 不可用保留意图，预算耗尽后受权条件重试；Costing 停机期间只显示已发布，恢复后出现业务接受与完成 |
| HTTP / 真网关 | 匿名 401、无权限 403、授权成功、注销后旧会话 401；无效分页/目标/时间 400；版本或重试竞争 409；原 API 统一包装不退化 |
| 启动/迁移/重启 | 独立 Scheduling schema 和迁移；普通启动不迁移；未迁移拒绝；DB 故障 ready=503 而 live 可应答；计划定义、启停、下一时刻与历史重启不丢 |
| 现有 Costing → Pricing | 新成本输入最终反映到价格；相同输入重算不伪造领域版本；原租约、Superseded 和 Redis 缓存失效行为仍成立 |

本轮研究可支持开始实现。合并前最关键的阻断风险是：**源端先推进后丢消息、两扫描器逻辑重复、重试生成新身份、Inbox 与业务任务分开提交、同宿主 Outbox 绑定错误、Delivered 被误报为业务成功，以及只认证未执行权限/会话检查**。这些均能通过上表的公开行为和真实故障注入验证；增加代码目录或只有单元测试绿不能替代它们。
