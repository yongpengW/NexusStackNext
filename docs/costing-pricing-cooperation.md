# 成本 → 定价参考链路

HTTP 中的 Int64（ID、version、epoch、size 等）均返回十进制字符串；请求优先原样回传字符串，精确数字输入继续兼容。见 [HTTP Int64 契约](http-int64-contract.md)。

[本轮票据](https://github.com/yongpengW/NexusStackNext/issues/28)。演示公式为 Costing 的采购成本加单位运费，
以及 Pricing 的单位成本除以一减费率。这些公式不代表 PoS 生产规则。

## 配置和启动

准备两个独立 PostgreSQL 数据库，分别配置 `ConnectionStrings__Costing`、`ConnectionStrings__Pricing`。
配置放在被忽略的 `env/*.dev` 或部署环境中，凭据不进入命令参数。两个宿主和网关使用相同的
`Jwt__SigningKey`、`Jwt__Issuer`、`Jwt__Audience`；Issuer / Audience 默认采用 `nexusstack`。

先显式迁移；普通启动只检查，不自动建表：

```powershell
dotnet run --project src/Hosts/NexusStackNext.CostingHost -- migrate-costing
dotnet run --project src/Hosts/NexusStackNext.PricingHost -- migrate-pricing
```

两边设置相同的 `RabbitMq__HostName`、`RabbitMq__Port`、`RabbitMq__UserName`、`RabbitMq__Password`、
`RabbitMq__VirtualHost`、`RabbitMq__ExchangeName`。显式开启 `Costing__Messaging__Enabled=true`、
`Pricing__Messaging__Enabled=true`；默认关闭，可独立演示本地计算。

Costing 开启消息后也会订阅 Scheduling 的计划触发；配置、迁移、接受结论和固定间隔语义见[持久调度](durable-scheduling.md)。
消费组 `Costing__Scheduling__ConsumerName` 默认 `costing-schedules`；独立测试环境应同时隔离交换机和消费组。

```powershell
dotnet run --project src/Hosts/NexusStackNext.PricingHost -- --urls http://127.0.0.1:5192
dotnet run --project src/Hosts/NexusStackNext.CostingHost -- --urls http://127.0.0.1:5193
# 网关环境中设置 Gateway__RouteTablePath=routes.business.json
dotnet run --project src/Gateway/NexusStackNext.Gateway -- --urls http://127.0.0.1:5190
```

Pricing 声明自己的持久队列、绑定与死信队列，建议先启动。生产者先启动时，不可路由事件在 Outbox 中重试，
预算耗尽后可人工重投。消费端名 `Pricing__Messaging__ConsumerName` 默认 `pricing-cost`，重启时保持稳定；
改名会创建另一份订阅。不同环境使用不同 virtual host 或消费端名称；只改 exchange 不能隔离队列。

网关默认路由仍只有平台；`routes.pricing.json` 用于单独定价，`routes.business.json` 加入成本并完整保留既有配置。
生产编排只发布网关端口。这里的 loopback 端口用于本机测试，多机高可用最后另开一轮。

Aspire 本地编排可同时设置 `NEXUSSTACK_PRICING_DB`、`NEXUSSTACK_COSTING_DB`，并提供上述完整 `RabbitMq__*` 配置；
它会显式启用两边消息、选择 business 路由。仅配置 Pricing 数据库时仍是上一轮的独立定价模式。

## HTTP 使用

所有接口均需有效根操作者令牌，使用统一返回格式。

三个手工入口支持可选 `delaySeconds`；两个上下文提供有界列表、条件取消和内部续租接口，
准确的状态、升级与竞争语义见[业务任务管理](business-task-management.md)。

1. `POST /api/costing/cost`：`requestId`、`itemId`、`expectedVersion`（创建为 0）、`purchaseCost`、`freightCost`。
   返回 202 和持久任务。重试保留 requestId，相同标识不同内容返回 409。
2. `GET /api/costing/tasks/{id}` 查计算及历史，`GET /api/costing/items/{id}` 查成本及版本。
3. `GET /api/costing/tasks/{id}/delivery` 查 Pending / Delivered / DeadLettered；尚无计算结果时返回 404。
4. `GET /api/pricing/items/{id}` 查成本、费率、`costingRevision` 和结果；比较 `inputRevision` / `calculatedRevision` 判断新鲜度。
5. `POST /api/pricing/fee`：`requestId`、`itemId`、`expectedVersion`、`feeRate`，只修改费率并重算。

首次成本事件创建对象时费率为零。已有人工定价对象接纳第一条成本事件后，由 Costing 接管成本、保留本地费率；
此后 `/api/pricing/cost` 覆盖不同成本返回 `pricing.cost_owned_by_costing` / 409。正式业务须补充渠道与定价政策。

## 来源事实容量治理（#101 实施中）

Costing 与 Pricing 各自提供 `GET /api/<context>/audit-capacity` 和同路径的 `PUT`，
沿用业务样板的根操作者限制。GET 返回业务额度、`policyRevision` 和独立 `controlCapacity`；
PUT 仅接受 `requestId`、`expectedPolicyRevision`、`maxRecords`、`maxPayloadBytes`、
`maxRecordPayloadBytes` 和固定 `reason=operator-adjustment`。Int64 继续用十进制字符串。
操作者、所属 schema 与事件名由服务端确定，请求不能改写这些归属。

版本从 1 开始，仅实际额度变化递增。相同请求、操作者与内容重放原裁决，先于版本比较；
身份复用或 CAS 冲突返回 409。允许降额到已有占用以下，但不删除已有事实；新业务写入继续受到背压。
变化记录为各自 Contracts 的 `fact-capacity-policy-changed.v1`，与业务事实计量分开。
控制池默认 1000 条、16MiB 总量、16KiB 单条，空操作也保存有限凭据；控制池满时明确拒绝调整。

两来源通过正常增量迁移保存策略与控制证据，重启不覆盖已有策略。有控制历史时拒绝破坏性 Down。
策略管理采用独立本地事务，不提交调用者的业务工作，也不修改原业务版本、已接受任务或报价缓存。
七天仅是凭据最早保留期；待投递与死信不能因到期删除，已交付控制副本另至少保留二十四小时。
中央 typed 数值调查已通过真实 MQ 验证：中央离线时提交，生产者退出后接收，中央进程重启后保留数值证据。
工作区已接入 `Costing:AuditDelivery:PolicyMaintenance` / `Pricing:AuditDelivery:PolicyMaintenance`，
各自在独立数据库清理自己的控制证据；诊断为 `costing-policy-cleanup` / `pricing-policy-cleanup`，参数见[共同维护说明](committed-auditing.md)。其余完整资格仍在 #101 中待办，
目前不能把本节接口实现视为整套治理已经交付。完整进展见[本地开发状态](handoff-2026-10-03.md)。

## 故障恢复

| 故障 | 行为 | 恢复 |
|---|---|---|
| 计算进程终止 | 持久租约过期后可接管 | 重启所属宿主 |
| 成本或 Outbox 写入失败 | 成本结果、任务终态、事件一起回滚 | 有限自动重试或人工重试 |
| broker 不可用或无绑定 | 成本可计算；投递重试，预算耗尽进入 DeadLettered | 修复后人工重投 |
| Pricing 数据库暂时不可用 | 原消息不被确认，退避重投 | 修复数据库 |
| 消费事务中进程被杀 | Inbox、业务与任务一起回滚 | 重启后 broker 重投 |
| 提交成功后 ACK 丢失 | 重投被已提交 Inbox 吸收 | 无需人工操作 |
| 旧版本晚到 | 不覆盖新成本，不登记多余计算 | 无需人工操作 |
| 同一消息 ID 内容改变 | 载荷指纹不符，明确拒绝并隔离 | 修复生产者，使用正确的事件身份与内容 |
| 非法事件 | 确认搬入消费端 `.dead` 队列 | 检查契约后经 broker 管理工具重投，不删除 Inbox |

计算失败：`POST /api/costing/tasks/{id}/retry`，正文含查询到的 `expectedEpoch`。
投递失败：`POST /api/costing/tasks/{id}/delivery/retry`，正文含查询到的 `expectedDeadLetteredAt`，消息 ID 保留。
计算完成、broker 确认、下游完成是三个状态；Delivered 只表示 broker 确认。

`/health/live` 只查进程；`/health/ready` 查数据库与迁移，启用消息时还查 broker 认证及交换机。
它不证明下游完成或死信为空，还需观察投递查询和 broker 指标。当前每个服务配置一个投递循环；
来源事实的已确认副本有独立保留清理；多实例投递协调、普通业务消息保留及完整运维面板留后续运营能力。

## 成本提交事实容量

Costing 的 PostgreSQL `fact_capacity` 随正常增量迁移创建，回填全部保留的 `CostSheetCommittedV1` 条数及 UTF-8 字节。
协议复用冻结的 [V1](adr/0025-context-owned-fact-capacity.md)，数据库持有额度和占用，宿主启动及重复迁移不覆盖策略。
普通 `CostCalculatedV1` 不占事实额度；默认额度为十万条、总载荷 256 MiB、单条 16 KiB。

创建或改变成本输入遇到容量拒绝时，成本版本、输入修订、任务受理与事实一起回滚，HTTP 返回
`503 / costing.audit_capacity_exhausted`。结果提交的拒绝还会回滚计算修订、任务终态及普通结果消息，
`CompleteCostingWork` 返回失败，不能作为完成或租约丢失解释。执行观察保留失败；worker 识别稳定错误码，
通过原有失败协议安排有限重试，预算耗尽后沿用 `expectedEpoch` 人工重驱。策略恢复或过期清理后可重试，
重复完成不重复提交结果。任务失败查询仍沿用既有 `costing.calculation_failed` 错误码，具体容量拒绝可由应用结果及 worker 日志区分。

重复请求、相同成本输入/计算结果和只受理既有成本快照的计划消息没有新成本变化事实，不因额度满而被无差别拒绝。
交付确认不释放额度，默认保留七天后的已确认事实副本清理才释放；待投递、死信及普通结果消息保留。
容量策略修改审计、诊断与独立等待预算继续由 #64/#60 跟踪。

## 定价提交事实容量

Pricing 的增量迁移接入同一冻结 V1 协议，只计算 `PriceQuoteCommittedV1` 的保留条数与 UTF-8 载荷。
默认上限同 Costing；重复迁移与宿主重启保留数据库中的策略及占用。
手工成本、费率与计算结果遇到容量拒绝时，报价、版本/修订、任务和缓存失效待办同事务回滚，
HTTP 返回 `503 / pricing.audit_capacity_exhausted`。结果应用返回失败，执行观察记 failed；worker
沿用有限重试及 `expectedEpoch` 人工恢复，任务查询仍使用既有 `pricing.calculation_failed`，
具体容量拒绝通过应用结果和 worker 的稳定错误码区分。

首次上游成本消费同时产生 created 与 costing-applied 两条事实，整批准入；拒绝不提交 Inbox/指纹、报价或任务。
消费处理器返回失败，不能当成成功去重确认。当前宿主未配置消费重试档位，原消息经确认搬入 `.dead` 队列；
修复容量后通过 broker 管理工具按原消息身份与内容重驱，不删除 Inbox。搬运时确认源队列代表 broker 已保留消息，
不代表 Pricing 接纳成功。重复及旧版本等无报价变化的有效消费仍可接纳；更新上游修订即使金额相同也需要新事实额度。

拒绝不留下持久缓存失效意图，已有 Redis 值在重启后继续代表已提交报价。成功提交才产生失效待办，
Redis 掉线时仍可提交业务、走数据库查询；恢复及重启后沿用持久失效重试。交付确认不释放额度，
仅过期且已确认的事实清理释放；待投递、死信和普通消息保留。专门审计恢复与完整治理仍由 #64/#60 跟踪。

## 验证

测试使用临时独立数据库、真实 RabbitMQ 与宿主进程。结果经 HTTP / ISender / 消息端口断言，
SQL 用于隔离夹具的策略/历史数据准备、故障注入和确认屏障命中；业务结果由公开接口验证。覆盖网关入口、Outbox 提交后重启、消费事务中杀进程、重复/乱序、
租约及结果回滚。`CostingFactCapacityTests` 覆盖原子拒绝、字节回填与边界、竞争、清理、快照受理及真实宿主重启/人工恢复，
`CostingFactCapacityObservationTests` 验证失败与恢复成功各自的执行观察。`PricingFactCapacityTests` 覆盖整批准入、
UTF-8 升级边界、最后额度竞争、清理、有限重试及真实 broker 死信/重启重驱；
`PricingFactCapacityObservationTests` 验证失败与恢复操作独立且保留关联，`PricingCacheTests` 验证拒绝与成功的缓存语义。
Pricing 的最终发布资格以 #78 的完整检查、Linux CI 与独立评审为准。CI 在独立 runner 之间并行，各组独占依赖且组内串行；
这些不证明多节点容灾或生产容量。

实施中的策略迁移另由 `FactCapacityPolicyBusinessMigrationTests` 验证 Costing / Pricing：
升级前后分别启动实际业务宿主进程，公开查询的成本/报价、已接受任务及原业务事实保持，
三个旧额度和实际占用字节保留；重复迁移后原请求重放同凭据，有治理历史时真实 Down 明确拒绝。
迁移使用实际模块注册和公开 EF 元数据/`IMigrator`，不为测试暴露内部业务 DbContext。
这是 #101 的迁移阶段资格，不能替代整票授权、故障、完整检查和 Linux CI 验收。
