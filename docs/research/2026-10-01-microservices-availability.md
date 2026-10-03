# NSN 微服务与高可用设计：一手资料备忘

资料核验日期：2026-10-01。用途：为架构评估和后续验收提供技术边界；本文件不是新的 ADR，也不代表实现或生产验证已经完成。

当前约束：用户目前只有单机，生产部署方案尚未确定。沿用 [ADR-0013](../adr/0013-platform-capabilities-are-one-host.md) 的“平台模块化单体、未来业务微服务”和 [ADR-0014](../adr/0014-tokens-are-issued-locally.md) 的本地令牌签发决定。本文不选定 Kubernetes、云厂商、托管产品、可用性百分比或恢复时间。

## 1. DDD 边界与部署边界分别证明

**官方事实。** 限界上下文定义领域模型的适用边界，并不规定它必须是独立进程。微服务拥有自己的领域数据与逻辑，外部通过 API 或消息访问，不能直接操作其数据。服务拆分还应满足独立演进和部署，避免把频繁同步交互的功能机械拆开。[Microsoft：数据所有权](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/architect-microservice-container-applications/data-sovereignty-per-microservice)、[服务边界](https://learn.microsoft.com/en-us/azure/architecture/microservices/model/microservice-boundaries)

**NSN 建议。** 保留五个平台模块共同部署的既有决定，继续用程序集、schema、迁移归属和契约约束上下文。未来业务服务各自拥有数据、事务和任务记录；统一任务管理只汇总投影，不形成所有上下文共享读写的任务数据库。判断“真正微服务”的证据应包括：一个业务服务可以独立发布；其他服务不可用时，有明确的降级与恢复行为；没有跨上下文读表或隐含共同发布要求。进程数量不是验收标准。

## 2. Outbox/Inbox 的事务边界与幂等

**官方事实。** 持久 Outbox 将待发送消息与本地应用数据一起写入事务，提交后再投递；Inbox 跟踪消息处理记录。Broker 确认与消费者确认是两个独立阶段，前者不意味着业务处理完成。[MassTransit：事务 Outbox 的工作机制](https://masstransit.massient.com/concepts/outbox#transactional-outbox)、[RabbitMQ：两种确认的边界](https://www.rabbitmq.com/docs/confirms#are-publisher-confirms-related-to-consumer-delivery-acknowledgements)

**NSN 建议。** 采用下列可检验契约；此处引用 MassTransit 仅作为实现原理的一手依据，不构成引入该产品的决定。

- 一个聚合的业务变更与本上下文 Outbox 意图同事务提交。事务不能跨其他上下文数据库、Redis 或 RabbitMQ。
- 保留稳定消息 ID。发布成功但来不及标记已发送时允许重复发布，消费方按“消费者标识 + 消息 ID”去重；业务幂等键另行表达同一业务请求，不能用传输去重代替。
- 同一事务内完成 Inbox 记录与对应业务写入，提交成功后确认消费；唯一约束必须覆盖并发重复消费。去重记录保留期应覆盖允许的重放窗口。
- 发邮件、调用平台接口等外部副作用不在本地事务保护内，需要目标端幂等键、查询核对或补偿，不能宣称端到端 exactly-once。
- 长任务可以先可靠接收，再由持久任务状态推进；无论何种确认时机，进程死亡后都必须能恢复未完成工作。保留 PoS ERP 的分段进度、执行代次、人工业务重试；锁竞争和基础设施恢复与业务失败分别处理。

验收必须覆盖“数据库已提交但未发布”“发布已成功但确认丢失”“消费事务已提交但 ACK 丢失”三个不同窗口。仅测试正常收发不证明可靠性。

## 3. RabbitMQ Quorum Queue 保证有前提

**官方事实。** Quorum Queue 在消息复制到多数成员后发出 publisher confirm。队列需要多数成员可用；三成员可容忍一个成员故障，两成员不能容忍一个。官方数据安全保证针对已确认消息，并以前述多数成员未永久丢失为条件；未确认消息没有同等保证。[RabbitMQ：Quorum Queue 故障容忍与数据安全](https://www.rabbitmq.com/docs/quorum-queues#fault-tolerance-and-minimum-number-of-members-online)

**NSN 建议。** 关键持久队列应评估 quorum、持久消息、非自动删除、publisher confirm、不可路由消息处理与手动 ACK 的完整组合。重投要有退避、上限或可操作的隔离状态，避免失败消息立即无限回队。具体默认值应按实际 RabbitMQ 版本与负载测试确定，不从最新版文档直接抄配置。

同一物理机上运行三个 RabbitMQ 容器可以验证协议与进程故障，不能证明主机故障可用性；它们共享断电、磁盘与宿主机故障域。未来生产拓扑需另外验证成员分布、磁盘故障与网络分区。以上是保证边界，不是当前选定三台机器的部署决定。

## 4. Redis 租约负责协调，存储端负责拒绝过期执行者

**官方事实。** Redis 异步复制切主可能丢失尚未复制的锁，使两个客户端都认为持锁。`WAIT` 能降低丢失概率，但不会把 Redis 变成强一致系统。Redis 锁文档明确提醒使用 fencing token，并指出长时间运行的进程不能因自己仍存活就假定锁仍有效。[Redis：分布式锁](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/)、[Redis：复制与 WAIT 的限制](https://redis.io/docs/latest/operate/oss_and_stack/management/replication/)

**NSN 建议。** Redis 接口分清立即尝试获取、限时等待、租约时长和业务超时；用唯一持有者值校验续期/释放，丢失租约后发出取消。但取消是协作行为，进程暂停、网络断开后仍可能恢复并写入，所以关键写入必须由存储端校验单调执行代次或其他 fencing 条件，在写事务中拒绝旧执行者。只在写之前查询一次“是否仍持锁”存在检查与写入之间的竞态。

执行代次的发放与校验本身必须有可靠的原子性，不能把可在切主时回退的缓存计数直接当成安全证明。资源互斥的范围也要匹配写入范围：仅同一任务的 Attempt 校验不能自动隔离两个不同任务对同一商品的并发写入。普通缓存可以有降级策略；身份撤销与关键写入协调需要单独定义失败行为。

## 5. ASP.NET 多副本身份、缓存与安全重试

**官方事实。** JWT Bearer 的基本检查包括签名、签发方、受众和有效期。HybridCache 的 L1 位于各进程；一个实例的键/标签失效不会立即清除其他服务器的 L1，其并发回源合并也不跨实例。[Microsoft：JWT 验证](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)、[HybridCache 存储与失效范围](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0#cache-storage)

**NSN 建议。** 本地签发决定可以保留，但不能把标准验签等同于动态撤销。会话/权限版本要有共享的权威状态与明确检查点，缓存丢失或进程重启不得让已撤销身份恢复有效。ADR-0014 的“立即失效”必须通过两个实例交叉请求验证；若后续接受传播窗口，需明确修订承诺，不能用短 TTL 悄悄替换。认证依赖不可用时需要定义安全的拒绝或受限行为，不能静默接受未经核实的旧权限。

JWT 签名密钥的分发与轮换不同于 ASP.NET Data Protection。只有使用 Cookie、Session、Antiforgery 等依赖 Data Protection 的功能时，才需要对应共享密钥环配置。[Microsoft：Web Farm 与 Data Protection](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/web-farm?view=aspnetcore-10.0)

官方当前 JWT 指南优先推荐标准 OAuth/OIDC 流程及非对称签名；现有自管令牌生命周期仍需专门审查轮换、重放检测、撤销和密钥责任。该资料与 ADR-0014 的取舍差异应在后续评估中显式讨论，本备忘不替换身份方案。[Microsoft：令牌创建建议](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0#recommended-approaches-to-create-a-jwt)

**安全重试。** .NET 标准 HTTP resilience handler 默认重试所有方法；官方特别提醒 POST 等写操作可能重复执行，提供关闭不安全方法重试的配置。EF Core 也明确指出连接在提交时中断会使提交结果未知，盲目重试可能造成重复或损坏。[Microsoft：HTTP 重试](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience#disable-retries-for-a-given-list-of-http-methods)、[EF Core：提交结果未知](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue)

因此建议默认只重试已证明安全的操作；业务命令使用稳定幂等键和结果核对，设置总时间预算、退避与并发上限。不要让网关、HTTP 客户端、数据库和消息层各自无限重试同一请求。

## 6. 以业务旅程、恢复证据和故障域验收高可用

**官方事实。** SLO 应从关键业务流程推导；RTO 表达可接受的恢复时间，RPO 表达可接受的数据损失时间窗口，未经恢复验证不应承诺对应 SLA。冗余必须覆盖关键流程的计算、数据与网络层，并保留故障后的容量。[Microsoft：可靠性目标](https://learn.microsoft.com/en-us/azure/well-architected/reliability/metrics)、[冗余设计](https://learn.microsoft.com/en-us/azure/well-architected/reliability/redundancy)

PostgreSQL 流复制默认异步，故障切换时可能损失尚未复制的已提交事务；同步复制降低该风险，但增加提交等待，对备机数量和可用性有要求。备份与 WAL 归档提供时间点恢复能力，仍需实际恢复校验。[PostgreSQL：流复制](https://www.postgresql.org/docs/current/warm-standby.html#STREAMING-REPLICATION)、[同步复制](https://www.postgresql.org/docs/current/warm-standby.html#SYNCHRONOUS-REPLICATION)、[PITR](https://www.postgresql.org/docs/current/continuous-archiving.html)

**建议的证据分层。** 以下是待执行的验收设计，不是已通过的测试记录。

| 验收对象 | 单机阶段可获得的证据 | 生产 HA 仍需补充 |
| --- | --- | --- |
| 领域与契约 | 不跨上下文读表、独立业务发布样本、契约兼容性 | 真实发布/回滚及不同版本共存 |
| 身份与权限 | 两实例交叉登录、撤销、刷新重放、重启不复活旧会话 | 共享存储故障、密钥轮换和故障切换 |
| 消息与任务 | 三个提交/确认窗口、重复/乱序、进程中断后恢复 | Broker 多数派故障、网络分区、积压恢复容量 |
| Redis 协调 | 暂停旧执行者直至租约过期，再恢复写入仍被拒绝 | Redis 切主与复制回退、网络分区 |
| 用户旅程 | 经网关提交任务、查进度、重试失败项，消费者重启后继续 | 网关/宿主故障域隔离及故障后容量 |
| 数据恢复 | 在隔离环境还原备份，校验业务数据和任务状态 | 独立故障域备份、真实恢复计时、数据损失核对 |

目前可以承诺推进“单机可恢复、接口支持多副本、业务效果可去重、恢复过程有证据”的能力建设；只有一台机器时，不能承诺主机故障后服务继续在线。未来生产方案确定后，再为具体业务旅程选择 SLO、RTO、RPO、容错范围和成本，并执行相应演练。架构代码、组件配置和部署拓扑三者都要满足条件。

## 研究范围

本轮读取既有 ADR，并核验上述官方技术资料；只新增本研究备忘。未访问生产或共享数据库，未启动消费者，未修改运行时代码、ADR 或仓库规则，未执行故障注入。官方在线文档会变化，产品版本选定后需按对应版本再次核对配置与保证。
