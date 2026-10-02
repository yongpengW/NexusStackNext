# 事务消息消费：Costing → Pricing 的可靠接纳边界

核验日期：2026-10-02。客户端事实固定到 RabbitMQ.Client **7.2.2** 源码；数据库语义引用 PostgreSQL 18，部署版本另行核对。本文是实现建议，不是已通过的验收，也不承诺多机高可用。

**建议：业务处理器在自己的数据库中原子提交 Inbox、一个业务聚合和任务登记；消费者只在提交成功后 ACK。发布确认和数据库事务各守一段边界，稳定消息 ID 将两段之间可能发生的重复收敛。**

## 1. 当前实现的缺口

以下为本轮开始时读取 [RabbitMqConsumer](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Events/RabbitMq/RabbitMqConsumer.cs) 得到的仓库事实，后续改造后不应当作现状说明：

| 当前路径 | 故障窗口 | 最小修法 |
|---|---|---|
| `TryBeginProcessingAsync` 单独占 Inbox，随后调用业务；失败才 `ReleaseAsync` | 占位提交后进程退出，重投被当成成功处理过 | 去重移入所属上下文的业务事务；运输层不预先提交 Inbox |
| 无 confirms 的消费通道执行 `MoveToAsync`，`BasicPublishAsync` 返回后 ACK 原消息 | 发布仅写入连接、消息尚未被 broker 确认；返回消息亦可能未被处理 | 搬运使用 confirms + tracking + mandatory，确认成功后才 ACK |
| 类注释声称处理 `CallbackExceptionAsync`，代码只订阅关闭事件 | Inbox、搬运或 ACK 抛错后没有明确恢复；prefetch=1 时剩余消息可能一直等待 | 接住完整消费回调边界；唤醒外层恢复循环、关闭旧通道，让未 ACK 消息重投 |
| 无效 MessageId 被替换成新 Guid；重投未复制 Timestamp | 同一坏消息每次获得新身份；原始发生时间丢失 | 无效身份作为契约错误处理；有效事件重投保留身份、时间及业务元数据 |

第一项结论来自上述实际执行顺序与关系事务原子性的组合推论：两个独立提交不构成一个原子操作。[EF Core：事务](https://learn.microsoft.com/en-us/ef/core/saving/transactions)

## 2. 发布确认到底确认了什么

7.2.2 的 `CreateChannelOptions` 默认同时关闭 `PublisherConfirmationsEnabled` 和 `PublisherConfirmationTrackingEnabled`。启用二者后，`BasicPublishAsync` 才会等待其确认任务；broker NACK 或相关 mandatory return 会令任务以发布异常失败。不要只设置 mandatory 后把正常返回解释成可靠投递。[通道选项源码](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/v7.2.2/projects/RabbitMQ.Client/CreateChannelOptions.cs)、[发布源码](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/v7.2.2/projects/RabbitMQ.Client/Impl/Channel.BasicPublish.cs)、[确认跟踪源码](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/v7.2.2/projects/RabbitMQ.Client/Impl/Channel.PublisherConfirms.cs)

```csharp
var options = new CreateChannelOptions(
    publisherConfirmationsEnabled: true,
    publisherConfirmationTrackingEnabled: true);
```

建议为单次确认设置有界取消时间。超时、断线或发布异常时不 ACK 原消息；关闭原通道后允许重新投递。若发布已成功但随后 ACK 丢失，目标队列和原队列可能都出现消息，故去重依然必要。confirm 对持久消息进入持久队列表示持久化确认；它不表示 Pricing 已经提交业务结果。mandatory 还用于发现没有路由目标的发布：不可路由消息也可能得到 confirm，因此两者不能互相替代。[RabbitMQ：确认时机与保证](https://www.rabbitmq.com/docs/confirms#when-publishes-are-confirmed)

Costing 的建议顺序是“成本计算结果 + 稳定 ID 的 Outbox 同事务提交 → 发布并等待确认 → 单独标记已投递”。最后一步失败只允许造成同 ID 重投；不能在每次发布时重新生成事件 ID。这是本仓基于上述确认语义采用的应用协议，不是 broker 提供的跨库事务。

## 3. Inbox 是已接纳凭证，归业务事务所有

建议处理一次有效 `CostCalculatedV1` 时使用 Pricing 自己的一个 `DbContext` 和显式本地事务：

1. 校验事件名、版本、MessageId、载荷身份与数值；不把契约错误当成数据库暂时故障。
2. 插入带唯一键 `(ConsumerName, EventName, MessageId)` 的 Inbox 记录。
3. 仅插入成功者加载并锁定目标聚合，检查来源版本；应用新成本并登记本地重算任务。
4. 提交全部变更，然后返回已接纳；消费者随后 ACK。失败或进程退出使全部回滚。

可以在该事务内执行参数化 `INSERT ... ON CONFLICT (consumer_name, event_name, message_id) DO NOTHING RETURNING message_id`。返回一行才获得处理权；返回零行意味着该唯一键已被其他提交占用。不要使用“先 SELECT 不存在，再 INSERT”作为并发去重协议。[PostgreSQL：INSERT / ON CONFLICT](https://www.postgresql.org/docs/18/sql-insert.html#SQL-ON-CONFLICT)

并发相同消息会在唯一索引处等待先行事务结束：对方回滚则后来者可插入，对方提交则冲突成立。因此 Inbox 占位和业务一同回滚时，无须另做“失败删除名额”的补偿动作。该保证来自数据库，不依赖进程内锁。[PostgreSQL：唯一性检查](https://www.postgresql.org/docs/18/index-unique-checks.html)

建议使用默认 Read Committed 配合上述唯一插入和聚合并发保护。`DO NOTHING` 的冲突记录未必在该 INSERT 自己的快照中可见；若需要核对已有消息载荷，应另发一次查询，避免把单语句 CTE 的“插入或读取”空结果误判为不存在。同 ID 不同载荷应作为协议冲突，不能静默吞掉。[PostgreSQL：Read Committed](https://www.postgresql.org/docs/18/transaction-iso.html#XACT-READ-COMMITTED)

EF 的一次 `SaveChanges` 原子性不能自动覆盖前面的独立 SQL，故插入 Inbox 前就要 `BeginTransactionAsync`。若拆成多个 `DbContext`，必须共享同一个连接和事务；本轮优先一个上下文。提交结果因断线而未知时，返回重试，让稳定唯一键决定重投是否需要再执行；不要凭异常推断数据库必定未提交。[EF Core：显式事务和共享事务](https://learn.microsoft.com/en-us/ef/core/saving/transactions#controlling-transactions)、[EF Core：提交失败与幂等性](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue)

**业务建议。** 消息 ID 去重只解决同一事件重复；不同消息 ID 的旧 Costing 版本仍须由 Pricing 聚合拒绝回退。来源版本比较与更新必须共享聚合并发保护；重复或旧版本可以提交 Inbox 后成功 ACK，但不得再次创建任务或增加业务 Version。计算由提交后的本地任务 worker 执行，消费事务保持短小。

## 4. 回调与重连必须有一个明确的负责人

7.2.2 的消费分发器捕获处理器异常后调用 `OnCallbackExceptionAsync`，没有自动替应用 NACK，也没有在这条异常路径主动关闭通道。因此不能把“回调抛出”当成“消息已安排重试”。建议回调异常事件只发恢复信号，清理由外层循环执行；避免在回调中等待会反过来等待消费分发结束的整套关闭过程。[消费分发器源码](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/v7.2.2/projects/RabbitMQ.Client/ConsumerDispatching/AsyncConsumerDispatcher.cs)

客户端自动恢复默认开启；但首次连接失败和通道级协议错误不靠它恢复，断线期间的发布也不会被自动缓冲重发。建议沿用本仓显式恢复循环时设置 `AutomaticRecoveryEnabled = false`，由该循环管理连接、拓扑声明、消费订阅、故障退避与资源清理，避免与客户端恢复并行接管。[7.2.2 ConnectionFactory](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/v7.2.2/projects/RabbitMQ.Client/ConnectionFactory.cs)、[官方恢复限制](https://www.rabbitmq.com/client-libraries/dotnet-api-guide#recovery)

回调应捕获创建该订阅时的 channel 和恢复信号，不从可被重连替换的全局字段寻找 ACK 通道。delivery tag 仅属于原通道；换通道确认会出协议错误。关闭通道会重新入队未确认消息；停机也应通过关闭通道完成这一职责。NACK requeue 可能立即再次投递，须有退避，不能故障时忙循环。[RabbitMQ：ACK 通道与重投](https://www.rabbitmq.com/docs/confirms#consumer-acks-double-acking)

继续使用每通道单处理器、prefetch=1 可简化本轮并发控制；读取消息体必须在回调结束前完成复制或反序列化。保留原始 MessageId、Timestamp、Type、CorrelationId 和需要传播的 headers，重试次数是运输元数据，不得改写业务身份。[.NET 客户端：内存和并发要求](https://www.rabbitmq.com/client-libraries/dotnet-api-guide#consumer-memory-safety)

## 5. TTL 重试与高可用的边界

当前经典 retry queue 的 TTL 到期后经 DLX 返回主队列，属于 broker 内部再次发布。默认 DLX 没有内部 publisher confirms；目标不可用时可能丢失。客户端对“搬入 retry queue”启用 confirms，不能补上随后 DLX 转发的保证。quorum queue 的 at-least-once dead-lettering 是另一套明确配置，不由“队列 durable”自动获得。[RabbitMQ：死信安全性](https://www.rabbitmq.com/docs/dlx#safety)

**本轮建议。** 新业务链路优先“可靠接纳后由数据库任务重试”：数据库暂时不可用时保留原消息，短暂退避后在原通道 NACK requeue，或关闭并重建通道；格式错误等确定不可处理的消息经确认发布进入 DLQ；有效消息一旦接纳，执行失败次数、延期和人工重试都由本地任务表负责。这样该链路不依赖 TTL → DLX 来保存尚未接纳的业务意图。旧通用 TTL 重试能力若继续保留，文档必须明确上述边界。

本轮验证单机进程/依赖恢复、重复与乱序。数据库复制、broker quorum 副本、磁盘永久损坏、网络分区、备份恢复与多机部署演练留后续 HA 票据；“单机恢复通过”不能替代那些验收。

## 6. 建议的公开接口验收

- 成本计算结果提交时注入 Outbox 登记失败：查询不能看到半成品；恢复后重试可完成。
- 事件已发布、Outbox 尚未标记时重启生产者：相同事件可以重投，Pricing 最终只产生一次可观察状态转换。
- Pricing 接纳事务提交前失败/杀进程：恢复后事件仍能创建任务；提交后 ACK 前失败：重投不重复生效。
- 并发同消息、同 ID 不同载荷、不同 ID 的旧来源版本：分别验证幂等、协议冲突和不回退。
- retry/DLQ 目标不存在或确认失败：原消息不能被提前 ACK；恢复目标后仍能处理或隔离。
- 消费回调异常、首次连接失败、活动连接断开：真实 broker 与真实进程恢复后继续完成业务；不能只验证进程还活着。

以上是研究提出的验证清单，结果应从 HTTP / ISender 和真实消息边界观察；数据库触发器、断连等可用于故障注入，不把内部行数当成业务结果。
