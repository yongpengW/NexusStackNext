# 已提交事实审计

首条完整链路是全局设置变更：HTTP 授权 → Platform 保存配置与最小事实 → Outbox → RabbitMQ → Auditing 的 Inbox 与不可变记录。配置提交成功后，审计最终可查；回滚、版本冲突和空操作不会生成成功事实。操作观察描述执行尝试，本文的 AuditFact 描述已提交结果，两者不能互相替代。

自动 HTTP 操作记录另见[操作日志](operation-logging.md)：其 Auditing-owned 来源 journal 使用独立连接和事务，业务回滚不会撤销观察；它不替代本链路与业务状态同事务的 Platform Outbox。普通观察允许可见降级，关键事实持久写失败仍必须阻止对应业务提交。

## 配置与启动

### 来源事实副本维护

PostgreSQL 模式下，六个事实生产模块显式启动独立维护循环。配置前缀为 `<Context>:AuditDelivery:Cleanup`，
Context 取 Identity、Platform、Files、Scheduling、Costing 或 Pricing，每个上下文分别配置：

Identity、Platform、Files、Scheduling 的 Memory 模式使用相同策略、端口和自动维护循环；
它仍仅用于开发测试，未交付事实不具备跨进程恢复能力。

| 键 | 默认值与范围 |
|---|---|
| `Enabled` | `true`；关闭后台循环仍可保留诊断中的明确开关状态 |
| `DeliveredRetention` | `7.00:00:00`；1 小时至 365 天 |
| `BatchSize` | 500；1–1000 |
| `Interval` | `00:01:00`；1 秒至 1 小时 |
| `Timeout` | `00:00:05`；50 毫秒至 30 秒 |

只清理模块声明的审计事件、首次发布确认已到期且没有死信标记的副本；不删除待投递、死信、
业务交付消息、Inbox 或中央事实。每轮独立预算、一批事务，锁住的记录留待下一轮，故障不提交部分删除。
HealthCheckService 的 `<context>-fact-cleanup` 注册项提供 `enabled`、`cleanupFailures` 与 `cleanupDegraded`；
`cleanupRuns` / `deletedCopies` 记录本进程成功自动轮次及其返回的删除数量，不计手动端口调用或结果未知的失败轮次。
当前 `/health/logging` HTTP 入口只输出组合健康状态，不输出这些明细。
下一轮成功恢复当前状态，累计失败数保留。该清理不依赖 RabbitMQ 或业务任务 worker 是否开启。

六个上下文的 `CommittedFactCleanup` 迁移增加清理部分索引，先执行各自已有的独立迁移入口。
调整来源保留配置会作用于已有已确认副本，不能把它理解为中央事实的固定保留承诺。
Platform / Identity PostgreSQL 已有事实数量/字节容量准入；其他来源、Memory 准入、专门恢复及中央归档仍待完成。
本项不表示整个治理已交付。
设计与证据见 [ADR-0024](adr/0024-committed-fact-delivery-retention.md)。

### Platform 事实容量首条路径

`SettingFactCapacity` 迁移创建数据库所属额度账本与事务触发器。默认最多保留 100,000 条事实、
正文总量 256 MiB、单条正文 16 KiB，按 UTF-8 字节计算。待投递、死信和未过保留期的已确认记录都占用额度。
额度不足时设置写入返回 HTTP 503 / `platform.audit_capacity.exhausted`，业务与事实一起回滚；
同值空操作仍可成功，安全清理已确认副本后释放额度。其他业务消息不计入这个事实额度。
策略当前存于数据库，不受多宿主启动顺序影响；可审计调整入口、容量健康诊断与其他适配器尚在实施，
不要直接修改占用计数。该逻辑上限不等于数据库磁盘文件上限。
详见 [Platform ADR-0002](../src/Services/Platform/docs/adr/0002-setting-fact-capacity-shares-the-business-transaction.md)。

Identity 的 `IdentityFactCapacity` 独立迁移采用相同容量语义。一个命令的所有事实必须全部获得额度；
不足时整批与业务一起回滚，HTTP 返回 503 / `identity.audit_capacity.exhausted`。
失败不触发权限缓存失效，同作用域可重新发送命令；重复角色/菜单授权不消耗额度或额外失效缓存。
错误密码若无法保存失败次数及事实，返回容量错误，恢复后才保存失败计数并返回正常凭据错误。
详见 [Identity ADR-0006](../src/Services/Identity/docs/adr/0006-fact-capacity-rejects-the-whole-command.md)。

### 中央存储与消息摄入

Auditing 默认 PostgreSQL，独占 `auditing` schema 与迁移历史。配置 `ConnectionStrings:Auditing`（环境变量 `ConnectionStrings__Auditing`），可以与平台其他模块暂用同一个数据库，也可以指向独立数据库。每个上下文只读写自己的数据。

连接配置只存于被忽略的 `env/platform.dev` 或部署环境。先执行独立迁移，再启动宿主：

```powershell
pwsh -File scripts/migrate-auditing.ps1
# 部署环境直接运行已构建宿主：dotnet NexusStackNext.PlatformHost.dll migrate-auditing
```

迁移命令只读取本上下文连接，既不启动 HTTP、RabbitMQ 或配置中心，也不初始化账号。普通宿主启动不迁移；未迁移、数据库不可用或配置错误都会拒绝启动。仅 Development / Testing 可显式选择 `Auditing:Storage:Provider=Memory`；该模式重启丢失记录。

运行期的中央 Auditing 数据库和审计 broker 检查归 `/health/logging`，故障时报告 `Unhealthy`、HTTP 503；`/health/ready` 排除这些异步日志依赖，只反映处理业务所需的依赖，`/health/live` 仍只检查进程存活。日志探测慢或失败不能让网关摘除业务依赖仍可用的宿主；运维须单独监控日志入口。业务侧保存关键 AuditFact 的 Outbox 仍与业务状态同事务，写入失败照常阻止提交；中央消费不可用与来源事实未能提交是不同故障。完整分组与来源 journal 的降级语义见[操作日志](operation-logging.md#故障策略)。

宿主配置 `RabbitMQ` 后同时启动 Platform 的 Outbox 投递与 Auditing 消费。消费名称默认 `auditing.entries`，可通过 `Auditing:Messaging:ConsumerName` 设置；多副本必须使用同一个名称共享队列。`Auditing:Messaging:Enabled=false` 只用于显式停用消费者。未配置 broker 时，已提交事实保留在 Platform Outbox，待接入 broker 后投递，不能把这时的空查询解释为“从未发生业务变更”。

## 信任与内容边界

设置事实消费已知契约 `platform.setting-committed.v1`。来源固定为 `platform`，动作由版本契约中的操作映射；客体为设置键和提交版本。Actor 由已认证会话提供，发生时刻由来源服务时钟提供，接收时刻由 Auditing 时钟提供。没有用户身份的受信后台调用 Actor 为空。Trace 来自来源执行，correlation 可以是经过约束的调用关联；两者仅用于调查，不能作为认证凭据。

Identity 新增自己的 `Identity.Contracts` 和 `identity.entity-committed.v1`。当前接入 PostgreSQL 下的 User 账户维护、
角色分配/撤销、登录与会话状态，Role 名称/平台/授权，MenuTree 节点变化，ApiResource 登记及 RefreshToken 生命周期。
中央只接纳明确列出的客体/动作及规范化内部标识；不传用户名、密码、哈希、联系方式或自由文本撤销原因。
错误密码即使返回 400，只要失败计数提交就产生事实；因已锁定而直接拒绝、没有持久变化时不产生新事实。
事实由 Identity 保存适配器与业务状态在同一次保存中写入，失败时移除本次暂存事实，允许同一存储作用域安全重试。
事件不依赖领域事件是否恰好覆盖某条变化路径；各变化分类仍须逐项测试，不承诺任意新聚合会自动被正确分类。

权限关系使用明确动作：User 的 `role-assigned` / `role-revoked`，Role 的 `menu-granted` / `menu-revoked`。
每一项实际增删带一个 `relatedSubject`，包含 `context`、`type` 与内部 `id`；令牌事实的关联客体是所属 User。
创建聚合时已有的关系也会记录。整组替换只记录成员差异，不把 ORM 对相同成员的先删后加写成撤权再授权；
同一次提交的多条事实使用相同的聚合提交版本，全部与业务同事务写入，中央交付期间可能暂时只看到其中一部分。
消费端按客体/动作验证允许的关联类型和内部正整数标识，客户端不能通过额外请求字段指定该关系。

MenuTree 以树根为客体，用 `node-added`、`node-removed`、`node-moved`、`node-renamed`、`node-reordered`
及 `node-ancestry-changed` 区分节点变化，关联客体为 Menu。移动子树时，父节点改变的节点记为移动，
仅祖先路径改变的后代记为祖先变化；不传标题或完整路径。创建时已有的节点也记录，提交前删后重建按净内容差异分类。
ApiResource 登记可携带所属 Menu；没有菜单时关联为空。相同登录时间下清零失败计数/解除锁定也属于已提交的成功登录状态变化。

宿主显式启用 Identity 两种存储的 Outbox 投递，配置为 `Identity:Delivery`；未连接 broker 时事实保留在来源。
中央消费者名称使用 `Auditing:Messaging:ConsumerName` 加 `-identity` 后缀。Memory 以 scoped 工作副本形成提交边界，
版本与唯一性验证、行审计、业务变化和最小事实整批提交，错误密码的拒绝按命令判据保留安全变化。
`MemoryIdentityTransactionTests` 验证隔离、失败与取消、重复保存、集合净变化和竞争，真实 HTTP / MQ 中央交付由
`MemoryIdentityAuditJourneyTests` 验证。Memory 不保证未交付消息跨重启恢复；完整故障矩阵、投递恢复管理与容量治理仍未完成。
真实重启旅程还验证了权限、菜单与令牌的 12 条事实，以及重放的 rejected 观察和 sessions-revoked 事实同时可查；
这些证据不代表完整 Identity 审计已验收，详见[覆盖矩阵](committed-audit-coverage.md)。

Files 通过 `Files.Contracts` 的 `files.stored-file-committed.v1` 交付文件生命周期事实，来源固定为 `files`，
客体为 `stored-file` 与文件内部标识。固定动作是 `registered`、`stored`、`deletion-requested`、`cleanup-deferred`、
`bytes-removed`；登记与存储完成可以在同一次提交中发生，不能把请求删除解释为字节已经移除。
PostgreSQL 仓储比较本上下文保存前后的独立快照，保留乐观并发条件，与文件状态在同一事务中写入事实。
来源事实写失败会回滚元数据与整批事实；重试不能留下第一次失败暂存的消息。载荷不含文件名、内容、存储句柄或访问地址。

Files 两种存储的投递选项都是 `Files:Delivery`，中央消费者名称为 `Auditing:Messaging:ConsumerName` 加 `-files`。
`FilesCommittedAuditTests` 验证来源写入、重复状态、批次写失败回滚，以及生产者先离线保存、重启后经 RabbitMQ 到达中央的旅程。
Memory 适配器在同一临界区提交元数据、行审计、首次删除来源与整批事实，构造失败或取消不留下半成品。
`MemoryFilesCommittedAuditTests` 验证跨作用域快照、版本冲突、系统恢复身份及真实 MQ 交付。
Memory 的待投递事实会随进程丢失；来源结束后仍可接收已交给 broker 的事实，不代表来源具备重启恢复能力。
持续故障的容量治理与完整恢复矩阵尚未完成，不能把来源 Outbox 已存在当作全面验收。

Scheduling 的 `scheduling.plan-committed.v1` 覆盖计划创建、启停、规则变化、重新排期、失败退避和故障清除；
已登记的触发、合并与跳过分别为 triggered / coalesced / skipped，关联类型为 `schedule-decision`，标识为决定的 GUID。
直接推进状态的保存仅记 advanced。事实客体为 `scheduled-task`，只传标识和最终版本，不传编码、规则或目标载荷。
Memory 与 PostgreSQL 都在来源原子提交，中央消费者名称为 `Auditing:Messaging:ConsumerName` 加 `-scheduling`，
交付沿用 `Scheduling:Delivery`。后台 Actor 为空，当前操作与原始发起关系独立保留；失败退避的事实关联本次 failed 观察。
来源事实失败会回滚计划、决定、发生及业务 Outbox；重复决定、旧版本和空操作不会追加事实。
审计消息不能通过 Occurrence 重试端口恢复；来源容量及专门的审计恢复治理仍属于 #64 的未完成项。

Costing 的 `costing.cost-sheet-committed.v1` 记录成本对象的 created / inputs-changed / result-applied，
客体为 `cost-sheet` 及对象 GUID，保留提交版本，不传成本组成或计算金额。输入、任务受理及创建/变更事实同事务；
计算结果、任务终态、CostCalculatedV1 与结果应用事实同事务。空操作、旧版本和过期输入不生成对象变化事实。
有效重算可能仍发布数值相同的 CostCalculatedV1；这种业务交付不意味着对象又发生一次变化。
来源事实保存失败、取消及提交前租约过期都会撤销本次结果与新消息，新租约可安全接管。

Costing 宿主使用已有的 `Costing:Messaging:Enabled` 与 `Costing:Delivery` 控制两类消息交付，未启用交付时事实留在本地。
中央消费者名称为 `Auditing:Messaging:ConsumerName` 加 `-costing`。CostDelivery 管理接口只允许查询/重试成本业务结果，
不能用它管理审计消息。生产者重启与真实 RabbitMQ 已验证操作关联，容量及审计恢复治理尚未完成。
设计见 [Costing ADR-0003](../src/Services/Costing/docs/adr/0003-cost-sheet-facts-share-the-state-commit.md)。

Pricing 的 `pricing.price-quote-committed.v1` 记录 created / inputs-changed / costing-applied / result-applied，
客体为 `price-quote` GUID 与提交版本，不传成本、费率和计算金额。成本接纳事实引用 Costing 的 `cost-sheet`，
较新的上游版本即使金额相同仍产生该事实；重复消息、旧版本、拒绝与无变化结果不生成对象变化事实。
报价、任务、Inbox 及新事实同事务，事实写入失败时一起回滚。消息消费和后台计算的 Actor 均为系统。
`Pricing:Messaging:Enabled` 启用成本消费及事实发布，`Pricing:Delivery` 配置来源 Outbox；中央消费者后缀为 `-pricing`。
设计与验收见 [Pricing ADR-0003](../src/Services/Pricing/docs/adr/0003-price-facts-and-message-acceptance.md)，容量与恢复治理仍未完成。

可选 `execution` 保留提交所在操作的来源/标识、根操作与原发起人，与事实一起进入所属业务 Outbox。
该来源指执行宿主，`fact.source` 则指拥有业务数据的上下文。通过 `/api/auditing/operations` 读取对应观察；
查不到观察仍保留事实，不能推断回滚，也不能补造 Started / Finished。旧事实没有关联时保持空，重投不能为它补写关联。
后台原发起人与当前 Actor 分开，身份与查询权限仍从有效会话取得。

关联客体作为事实内容参与指纹；同一消息改换关联客体属于身份冲突。旧事实没有关联时保持空和旧指纹，
不能在重投时根据当前业务表补写关系。设计依据见 [Auditing ADR-0004](../src/Services/Auditing/docs/adr/0004-facts-reference-related-subjects.md)。

记录不包含配置值、说明、口令、令牌、文件原文或完整请求体。全部环境关闭 `POST /api/auditing/entries`，包括 root 账号。HTTP 客户端无法自己声明 Actor 或 Source 写审计。

消息可信性的边界是 broker 发布权限：服务身份与 RabbitMQ ACL 必须限制谁能向设置事实的路由键发布，并保护传输与凭据。类型名和固定 Source 并不是数字签名；持有发布凭据的进程仍有能力伪造契约。当前平台宿主共用其配置的 RabbitMQ 身份，独立服务部署时要分配受限身份；多机部署加固等待用户决定后再安排。

Auditing 在同一事务内写入 Inbox、内容指纹和记录。相同事件名 / 消息 ID / 内容再次到达，返回重复；同身份不同内容返回 `auditing.message_conflict`，不得覆盖原记录。存储失败抛出并由 broker 保留重投，进程中断不留下只登记去重却丢失事实的半成品。PostgreSQL 时间保存到微秒，指纹基于收到的原始类型化事实，精确重复仍可识别。

## 调查与恢复

在 Identity 中按下列路径和 HTTP 方法登记资源，授予对应角色；仅有调查查询权限不会获得重试权限。已注销或禁用会话不能继续读取，root 同样需要有效会话。

| 接口 | 权限资源 | 行为 |
|---|---|---|
| `GET /api/auditing/entries?page=1&limit=50` | `/api/auditing/entries` + GET | 默认最近七天，接收时间、ID 倒序；page 1–1000，limit 1–100 |
| `GET /api/platform/audit-deliveries?state=DeadLettered&limit=50` | `/api/platform/audit-deliveries` + GET | 状态支持 Pending / Delivered / DeadLettered，limit 1–100，最旧优先；返回消息 ID、次数和投递时间状态，不返回消息正文 |
| `POST /api/platform/audit-deliveries/{messageId}/retry` | `/api/platform/audit-deliveries/{messageId}/retry` + POST | 请求 `expectedDeadLetteredAt` 使用查询得到的时间；仅重试该轮已耗尽投递，状态已变化返回 409 |

事实查询支持精确的 `source`、`action`、`subjectType`、`subjectId`、`actorId`、`traceId`、`correlationId`、
`operationId`、`operationSource`、`rootOperationId`、`rootSource`、`initiatorId`。`from` / `to` 使用来源发生时刻，
均含边界并规范化为 UTC；都省略时为最近七天，单边界按七天补齐，每次最多三十一天。旧数据仍保留，
按明确历史窗口调查；默认窗口不是保留或删除策略。

按关系调查时同时提供 `relatedContext`、`relatedSubjectType`、`relatedSubjectId`，缺少其中任何一项返回 400。
例如 `relatedContext=identity&relatedSubjectType=user&relatedSubjectId=42` 可以定位用户 42 关联的令牌事实；
三个字段精确匹配，不进行名称匹配或跨上下文 JOIN。该查询仍受时间窗口、分页和原有调查权限约束。

分页最多读取过滤结果的前 100000 条，总数只表示当前窗口和条件的匹配数。持续写入期间总数和分页不是数据库快照，
暂不提供历史导出。按任务调查先在操作查询中选择任务/轮次，再用操作来源和标识关联事实，不连接业务上下文的表。

Platform 的投递策略由 `Platform:Delivery` 配置，默认最多 8 次失败后保留为 DeadLettered，轮询间隔 2 秒。故障恢复后，受权操作员重新提交同一 ID；成功表示恢复为待投递，并不代表 Auditing 已保存。Delivered 表示 broker 已确认，最终落库以审计查询为准。重复提交或结果未知时先重新查询状态，不能生成新 ID 绕过去重。

业务侧 Outbox 耗尽与消费者死信是两个位置：前者通过上述接口恢复；消费者拒绝非法契约或身份冲突时沿既有 RabbitMQ 重试 / 死信拓扑处理，应排查来源并保留原记录，不能覆盖已接纳事实。数据库故障属于基础设施故障，消费者会重新排队，恢复后继续。

## 验证范围

`AuditBusinessJourneyTests` 使用真实 HTTP、RabbitMQ、临时 PostgreSQL 与真实宿主进程，覆盖提交后生产者重启、回滚 / 空操作 / 冲突、独立审计库断连、重复投递、Inbox 与记录之间崩溃、投递耗尽后的受权恢复。`AuditPersistenceJourneyTests` 覆盖独立迁移、失败事务、并发重复和跨宿主去重；`AuditAccessTests` 验证授权与三份实际网关路由；启动测试覆盖缺失配置和禁止生产 Memory。运行全量使用 `scripts/run-tests.ps1` 串行执行。

操作关联由真实生产者重启旅程验证；`AuditInvestigationTests` 在内存及 PostgreSQL 上检验精确查询、默认窗口与非法参数。
`CommittedFactCompatibilityTests` 使用旧版本源码独立编译出的冻结消息/指纹，检验迁移和重投不能改写事实。
`AuditInvestigationIndexTests` 在独立库的事实/观察表各放入 4000 条分布夹具，捕获两个公开存储端口生成的
计数及分页 SQL，通过 PostgreSQL `EXPLAIN` 检查客体、Actor、操作、根操作、任务轮次及事实时间查询的索引选择。
检查保留优化器正常的顺序扫描选项；它验证这些选择性查询的计划，实际容量和吞吐仍需按部署数据验收。
其他上下文的事实覆盖与容量治理仍按[覆盖矩阵](committed-audit-coverage.md)推进，不能将 Platform 样板当作全模块完成。
