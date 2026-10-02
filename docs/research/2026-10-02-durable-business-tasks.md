# 持久化业务异步任务与 Redis：一手资料研究

资料核验日期：2026-10-02。本文是设计研究，不是已接受 ADR，也不代表 SQL、崩溃恢复或多副本已验收。外部事实只引用 PostgreSQL、Microsoft、Redis、RabbitMQ 官方资料；“建议”是结合本仓约束作出的设计判断。SQL 以 PostgreSQL 18 文档为依据，实际部署版本须在实现时核对。

**建议起点：每个业务上下文在自己的数据库内持久化任务意图，用 PostgreSQL 短事务领取、租约与执行代次恢复工作，用业务幂等约束最终效果。单机先以宿主内 worker 执行，未来多副本沿用同一协议。Redis 可作为缓存和减少重复计算的协调工具；任务的可靠性不依赖 Redis 锁。**

## 1. 仓库现状与三种职责

本次读取了 [AGENTS.md](../../AGENTS.md)、[CONTEXT-MAP](../../CONTEXT-MAP.md)、[ADR-0013](../adr/0013-platform-capabilities-are-one-host.md)、[ADR-0017](../adr/0017-context-owned-command-transactions.md)、[ADR-0004](../adr/0004-contracts-and-events.md)。它们要求：上下文拥有数据，应用层拥有端口，宿主显式装配，跨上下文经契约与事件通信；平台模块共宿主、共库分 schema，未来业务上下文拥有独立库。

下表前两列是建议的职责划分，最后一列是本次源码观察：

| 概念 | 应回答的问题、数据归属 | 当前可核验位置 |
|---|---|---|
| 持久化业务任务 | 某次业务工作是否已接受、完成到哪、如何恢复；归执行该业务的上下文 | 本次检查的 Scheduling 没有业务任务领取/租约协议；不把它视作已有实现 |
| Outbox | 本上下文已提交的消息意图是否已投递；归生产消息的上下文 | [DomainEventOutboxInterceptor](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/DomainEventOutboxInterceptor.cs) 在保存时登记消息；[OutboxPublisher](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Events/OutboxPublisher.cs) 发布后另行标记 |
| 定时触发 | 何时产生一次触发、迟到是否补跑；归 Scheduling | [CONTEXT](../../src/Services/Scheduling/CONTEXT.md) 定义固定间隔、迟到不补跑；[ScheduleRunner](../../src/Services/Scheduling/NexusStackNext.Scheduling.Application/ScheduleRunner.cs) 推进执行时刻；[存储](../../src/Services/Scheduling/NexusStackNext.Scheduling.Infrastructure/InMemoryScheduledTaskStore.cs) 当前为内存 |

**建议。** 继承 NS AsyncTask 的易用目标：登记任务、查询状态、分段进度、重试与人工处理；不要由平台 Scheduling 连接所有业务数据库并执行所有业务处理器。统一管理页面可以消费状态投影，重试命令仍由任务所属上下文授权和执行。需要跨上下文时，建议采用“Scheduling 持久化触发与 Outbox → 契约事件 → 目标上下文 Inbox 与本地任务同事务登记”，不让“到期事件已发出”冒充“业务已经成功”。这遵循上述仓库边界，属于待实现方案。

**现状限制。** [EfOutboxStore.ReadPendingAsync](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/EfOutboxStore.cs) 是查询待投递记录，没有本研究要求的原子领取、租约和执行代次。不能直接把它的读后改协议复制为多副本任务协议；多副本 Outbox 本身也需要另行验证竞争、重复与状态覆盖。[Scheduling ADR-0001](../../src/Services/Scheduling/docs/adr/0001-next-run-advances-in-the-aggregate.md) 中“调度器无状态”只描述状态归属，不能单独证明触发与业务副作用的原子性。

## 2. 登记任务必须与业务变更同事务

**官方事实。** PostgreSQL 事务把多个步骤作为整体提交或回滚；EF Core 在支持事务的提供程序上，单次 `SaveChanges` 默认具有事务性。多个 `DbContext` 若要共享本地关系事务，必须共享 `DbConnection` 和 `DbTransaction`；仅指向同一数据库不足以证明同事务。[PostgreSQL：事务](https://www.postgresql.org/docs/18/tutorial-transactions.html)、[EF Core：事务](https://learn.microsoft.com/en-us/ef/core/saving/transactions#cross-context-transaction)

**建议。** 在本仓不引入跨库分布式事务的前提下，要求“业务成功 ⇒ 工作意图不会丢失”就必须把任务意图与业务数据登记在**同一业务数据库、同一事务**中。可以是本上下文任务表，或先登记本上下文 Outbox，再由接收方可靠创建自己的任务。后者提供最终一致，不是两个库同时提交。禁止用“先提交业务、再写平台任务库/Redis/发 RabbitMQ”表示原子登记；第二步失败就留下缺口。Microsoft 的集成事件示例同样把业务数据和待发布事件日志放进本地事务，再单独发布。[Microsoft：业务数据与集成事件的原子性](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/multi-container-microservice-net-applications/subscribe-events#designing-atomicity-and-resiliency-when-publishing-to-the-event-bus)

建议任务记录最小包含 `TaskId`、任务类型与载荷版本、稳定业务幂等键、状态、下次尝试时间、执行代次 `LeaseEpoch`、租约截止时间、进度/检查点、失败分类与次数。身份和必要业务参数按所属上下文授权后登记；不把令牌或连接配置放入载荷。

一个事务只修改一个业务聚合及其技术记录（任务意图、Inbox/Outbox、检查点）；任务记录不能成为跨多个业务聚合大事务的借口。若“任务”被建模为独立业务聚合，则需重新审查其与原业务聚合之间的最终一致边界，不能靠改表名绕过 [不变量 4](../../AGENTS.md)。

## 3. 领取用短事务，租约之外还要校验执行代次

**官方事实。** `FOR UPDATE SKIP LOCKED` 跳过已锁定行，官方明确列出队列式多消费者为适用场景，但它提供不一致视图，且不跳过所有表级锁。行锁在事务结束时释放。因此它保护领取事务，不保护事务提交后的长时间工作，也不能据此承诺严格 FIFO。[PostgreSQL：SELECT 锁定子句](https://www.postgresql.org/docs/18/sql-select.html#SQL-FOR-UPDATE-SHARE)、[PostgreSQL：行锁](https://www.postgresql.org/docs/18/explicit-locking.html#LOCKING-ROWS)

**建议的协议草图（未执行验证）。** 下面用固定示例 schema `business`；实际表归所属上下文，参数由数据库驱动绑定。只领取 worker 当下有容量处理的批次，避免尚未开始就租约到期。

```sql
WITH candidates AS (
    SELECT id
    FROM business.background_task
    WHERE (state = 'pending' AND next_attempt_at <= clock_timestamp())
       OR (state = 'running' AND lease_until <= clock_timestamp())
    ORDER BY next_attempt_at, id
    LIMIT @batch_size
    FOR UPDATE SKIP LOCKED
)
UPDATE business.background_task AS t
SET state = 'running',
    lease_epoch = t.lease_epoch + 1,
    lease_until = clock_timestamp() + @lease_duration,
    owner_id = @worker_id
FROM candidates AS c
WHERE t.id = c.id
RETURNING t.*;
```

提交领取事务后再计算、下载、分段处理。续租、进度、失败、完成都必须携带 `(TaskId, LeaseEpoch)`，要求当前仍为 `running` 且租约有效，并检查受影响行数。0 行表示失去本代次执行权，不能继续报告成功。`OwnerId` 用于诊断，`LeaseEpoch` 用于拒绝旧执行者，二者不混用。

**时间事实与建议。** PostgreSQL 的 `CURRENT_TIMESTAMP` 固定为事务开始时间，`clock_timestamp()` 取实际当前时间。建议租约统一按数据库时钟比较，避免各 worker 的时钟差；它仍是墙上时钟，租约用于恢复活性，正确性还依赖下面的代次校验。[PostgreSQL：当前时间函数](https://www.postgresql.org/docs/18/functions-datetime.html#FUNCTIONS-DATETIME-CURRENT)

**为什么只在任务表完成时检查代次不够。** 假设 A 的租约到期，B 领取新代次；A 已经提交业务数据，随后完成状态更新被拒绝。此时任务状态守住了，业务数据没有守住。Redis 官方也要求长时间执行者使用 fencing token，不能从进程仍存活推断锁仍有效。[Redis：一致性说明](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/#disclaimer-about-consistency)

**建议的本地提交边界：** 每个业务检查点使用新短事务，先锁定任务行，校验状态、代次、租约；在同一事务更新一个业务聚合及幂等记录，最后以相同代次和有效租约条件更新检查点或完成状态，0 行则整体回滚。锁一直持有到事务结束，领取者不能在该事务中途换代。不要在此事务里等待远程 HTTP 或运行长计算。这个方案依据 PostgreSQL 行锁与事务规则；其防护目标是“新代次已取得执行权后，旧代次不能再提交受保护写入”，不是强行终止旧进程。

PostgreSQL 在 Read Committed 下，会在等待并发更新后重新检查 `UPDATE` 的 `WHERE` 条件；EF Core 并发令牌也把原版本加入更新条件，0 行产生并发冲突。因此业务聚合仍应保留自己的 `Version` 检查，任务执行代次不能代替业务版本。[PostgreSQL：Read Committed](https://www.postgresql.org/docs/18/transaction-iso.html#XACT-READ-COMMITTED)、[EF Core：乐观并发](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)、[本仓 ADR-0011](../adr/0011-optimistic-concurrency-in-the-aggregate.md)

任务代次只在同一任务内单调递增。**建议：** 两条不同任务若能更新同一业务资源，另以业务状态机/版本约束冲突；确需同资源串行或 fencing 时，代次作用域必须覆盖该资源。不可拿任务 A 的 `Epoch=5` 与任务 B 的 `Epoch=2` 比较所有权。

## 4. 至少一次执行、业务幂等与合并分别建模

**官方事实。** RabbitMQ 发布确认丢失后重发可能造成重复，消费者需要去重或幂等。消费者确认发生在消费者已完成其责任之后；该责任可以是把消息可靠记录到数据存储。因此，消费方可在同事务保存 Inbox 与任务后 ACK，由持久任务继续工作，但 ACK 不表示业务已经完成。[RabbitMQ：可靠性与确认](https://www.rabbitmq.com/docs/reliability)

**建议。** 任务按“允许重复尝试、最终效果由幂等协议保护”设计。承诺可恢复和明确失败状态，不承诺每个任务最终成功。至少区分：

| 类型 | 建议幂等键与合并语义 |
|---|---|
| 每次都具有独立意义的操作：记账、扣款、发货指令 | 稳定业务操作 ID；同一操作重试复用 ID，不同操作不能按“客户/订单相同”吞并；与业务效果同事务保存执行记录 |
| 从当前真相重建派生结果：刷新商品索引、重新计算可重建汇总 | 可以按资源合并；记录 `RequestedRevision` 与 `ProcessedRevision`，运行中出现新请求必须留下后续工作；禁止新请求刚到就被旧 worker 标记完成 |
| 更新外部系统的目标状态：同步价格、库存快照 | 仅在目标协议支持版本/条件写/幂等键时采用最新状态合并；保证旧请求不能晚到后覆盖新状态；增量“加 3”不能直接当成快照“设为 3” |
| 大批量导入或导出 | 任务 ID 标识请求，检查点标识已提交分段；每段/每项有稳定幂等身份；重试次数不充当业务身份 |

以上是按业务语义作出的建议，不是数据库自动提供的合并能力。`TaskId`（一次工作）、`IdempotencyKey`（同一业务操作）、`CoalescingKey`（可合并资源）、`LeaseEpoch`（一次领取）应分开。PostgreSQL 唯一约束配合 `INSERT ... ON CONFLICT` 可作为并发登记的数据库机制；约束包含的租户、任务类型、操作 ID 及保留期限需要业务决定。[PostgreSQL：INSERT 与 ON CONFLICT](https://www.postgresql.org/docs/18/sql-insert.html#SQL-ON-CONFLICT)

**外部副作用边界。** 本地事务不能同时提交第三方 HTTP 的副作用。远端已成功、本地尚未记成功时崩溃，重试可能再次调用；本地任务代次也不能让一个不校验该代次的远端拒绝旧调用。这是本地事务边界与可重投机制的推论，不是外部 exactly-once 保证。[PostgreSQL：事务边界](https://www.postgresql.org/docs/18/tutorial-transactions.html)、[RabbitMQ：重复投递窗口](https://www.rabbitmq.com/docs/reliability#data-safety-on-the-publisher-side)

建议优先使用目标系统的稳定幂等键；重试继续使用原键。目标不支持幂等时，明确采用状态查询、对账或人工处理，允许显示“结果待核对”；不要一律当失败自动重发。数据库提交确认阶段断线也存在结果未知，EF 官方要求专门处理提交不确定性；因此业务幂等 ID 应在可重试操作外确定，不能每次重试重新生成。[EF Core：提交失败与幂等问题](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue)

## 5. Redis 的取舍

**官方事实。** Cache-Aside 不保证缓存与数据库始终一致。Redis 异步复制切主可能丢失锁记录；`WAIT` 降低风险，但不会把 Redis 变成强一致系统。Redis 锁必须校验自己的唯一值后才删除，不能无条件 `DEL`；这只防止误删别人的锁，不等于业务写入具有 fencing。[Microsoft：Cache-Aside](https://learn.microsoft.com/en-us/azure/architecture/patterns/cache-aside#problems-and-considerations)、[Redis：复制](https://redis.io/docs/latest/operate/oss_and_stack/management/replication/)、[Redis：分布式锁](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/)

| 用途 | 本项目建议 |
|---|---|
| 可重新计算的查询缓存 | 可引入 Redis；按上下文/租户/对象版本命名，设置过期时间、容量策略与受控回源；数据库仍是真相 |
| 减少缓存击穿、重复刷新 | 可用短租约作为性能协调；重复执行仍须安全；Redis 故障时通过限流/并发上限保护回源 |
| 任务唯一领取、库存或记账正确性 | 交由所属 PostgreSQL 的事务、唯一约束、版本与 fencing；不依赖 Redis 锁作唯一防线 |
| 多实例权限与会话缓存 | 延续 [ADR-0017](../adr/0017-context-owned-command-transactions.md) 的提交后失效原则；单独定义撤权时效，不能从 Redis 可用推断授权数据最新 |

建议先实现无 Redis 也可恢复的任务协议，再根据真实读热点加入缓存。增加 Redis 是性能和运维选择，不是从单实例扩到多实例的必要条件。若缓存失效需要跨进程可靠传播，使用提交时持久化的版本/事件；仅在提交后内存回调删除缓存，仍需考虑提交成功后进程退出的窗口。此处是对上述缓存一致性限制和本仓事务决定的设计推论。

## 6. 落地顺序、验收和未决问题

**建议的首个实现切片：** 选一个真实业务上下文和一种可验证任务，显式注册任务类型到处理器的映射；应用层端口表达登记/执行需求，PostgreSQL 适配器封装领取协议，宿主显式注册 worker。第二个业务上下文确实需要相同机制后再提取 `BuildingBlocks`，避免提前建立中央任务框架。[依据：架构不变量 3、7、8](../../AGENTS.md)

Worker 可使用 .NET `BackgroundService` 承载循环，但官方说明异常退出可能不调用 `StopAsync`，且 hosted service 不自动提供 DI scope。建议每次处理创建独立作用域，持久化恢复协议不依赖优雅停机；进程内队列仅用于唤醒，定期数据库扫描负责找回工作。[Microsoft：托管后台服务](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)

建议把基础设施不可用、暂时锁竞争、业务暂不可执行、永久业务拒绝、外部结果未知分别记录；退避、自动重试上限、人工继续/重试与取消都是显式状态迁移。至少记录队列最老等待时间、执行耗时、续租失败、过期接管次数、业务失败、隔离任务与可关联错误摘要。单机、多副本共用这组可观察行为。

实现验收应覆盖：

1. 业务提交前失败：业务与任务都不落库；业务提交后 worker 未唤醒即崩溃：扫描能恢复。
2. 两 worker 竞争同一任务：只有一个取得当前代次；另有独立任务仍可推进。
3. A 暂停到租约过期，B 接管，A 恢复：A 的业务写入、检查点、失败状态、续租与完成全部被拒绝，而非只拒绝最后一条状态更新。
4. 分段业务已提交、执行进程退出：重做该段不重复业务效果；外部成功但本地未知时进入预定核对路径。
5. 可合并任务运行时又来新请求：最新请求最终被处理；独立业务操作不被合并丢失。
6. 多个不同任务指向同一资源：业务版本/状态机保持正确；不能只测同 TaskId 重复。
7. Redis 停止或清空：已接受任务仍存在；缓存回源有明确容量边界。

这些是后续验收建议，本次没有运行数据库测试。按 [AGENTS.md](../../AGENTS.md) 在隔离数据库验证故障窗口，多工程测试使用串行脚本；单机多进程通过只证明 worker 竞争协议，不能证明主机或数据库故障高可用。

需要在首个业务用例中确定的事项：

- 第一种任务是什么，最长执行/检查点时长与预期吞吐是多少；哪些步骤有外部副作用？
- 哪些请求代表独立业务操作，哪些允许合并；是否要求同资源顺序，以及失败是否阻塞后续任务？
- 目标系统是否支持幂等键、版本条件写或结果查询；未知结果由谁核对？
- 自动重试、人工重试、取消、保留期和幂等记录保留窗口分别是什么？
- 实际 PostgreSQL/Redis/RabbitMQ 版本、部署副本、备份与故障恢复目标是什么？当前单机起步不预设答案。
