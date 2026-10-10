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
六个 PostgreSQL 事实来源已有数量/字节容量准入，四个平台 Memory 的对等实现由 #80 验收；专门恢复及中央归档仍待完成。
本项不表示整个治理已交付。
设计与证据见 [ADR-0024](adr/0024-committed-fact-delivery-retention.md)。

### Platform、Identity 与 Files 事实容量

`SettingFactCapacity` 迁移创建数据库所属额度账本与事务触发器。默认最多保留 100,000 条事实、
正文总量 256 MiB、单条正文 16 KiB，按 UTF-8 字节计算。待投递、死信和未过保留期的已确认记录都占用额度。
额度不足时设置写入返回 HTTP 503 / `platform.audit_capacity.exhausted`，业务与事实一起回滚；
同值空操作仍可成功，安全清理已确认副本后释放额度。其他业务消息不计入这个事实额度。
策略当前存于数据库，不受多宿主启动顺序影响；可审计调整在本分支按 #101 实施，见下方实施章节；受权只读诊断见下节，
不要直接修改占用计数。该逻辑上限不等于数据库磁盘文件上限。
详见 [Platform ADR-0002](../src/Services/Platform/docs/adr/0002-setting-fact-capacity-shares-the-business-transaction.md)。

Identity 的 `IdentityFactCapacity` 独立迁移采用相同容量语义。一个命令的所有事实必须全部获得额度；
不足时整批与业务一起回滚，HTTP 返回 503 / `identity.audit_capacity.exhausted`。
失败不触发权限缓存失效，同作用域可重新发送命令；重复角色/菜单授权不消耗额度或额外失效缓存。
错误密码若无法保存失败次数及事实，返回容量错误，恢复后才保存失败计数并返回正常凭据错误。
详见 [Identity ADR-0006](../src/Services/Identity/docs/adr/0006-fact-capacity-rejects-the-whole-command.md)。

Platform / Identity 通过 `SharedFactCapacity` 向前迁移接入冻结的共用 V1 协议，保留原表与策略。
Files 的 `FileFactCapacity` 迁移按既有事实回填独立额度账本。一次上传的两条事实整批准入，
不足时返回 503 / `files.audit_capacity.exhausted`。删除申请被拒绝时保留可读文件；受理后清除完成事实
无法提交时仍是可恢复的 202 待办。存储端口仅翻译精确的容量错误，其他故障继续按原异常传播。
共用规则与迁移冻结约束见 [ADR-0025](adr/0025-context-owned-fact-capacity.md)；Scheduling、Costing、Pricing 的 PostgreSQL 验收见[覆盖矩阵](committed-audit-coverage.md)。

### 显式开发模式的 Memory 事实容量

Platform、Identity、Files、Scheduling 分别从 `<Context>:AuditDelivery:MemoryCapacity` 装配自己的策略。
`MaxRecords` 默认 100000，`MaxPayloadBytes` 默认 268435456，`MaxRecordPayloadBytes` 默认 16384，
对应保留条数、正文 UTF-8 总字节和单条字节。三个值必须为正，单条不得超过总量；非法配置拒绝宿主装配。
这些是内存来源的初始策略配置，不覆盖 PostgreSQL 中的持久策略；本分支的受权调整接口见下方 #101 实施章节。

四个实际消费者共用 `InMemoryCommittedFactCapacity` 计量实现，各存储独占实例，并与其业务发布使用同一写锁。
批次全部构造完成后一次准入；业务发布成功才增加占用，过期确认副本实际删除才减少占用。
计量只读取当前写入或清理批次的载荷，不随保留消息数量重复扫描历史正文。
这不改变 Identity 原有工作副本和 Outbox 快照复制，也不宣称所有保存操作都是常数时间。
其他消息、投递确认、空操作和重放不消耗新事实额度；待投递和死信持续占用，不能靠清理逃逸。

容量拒绝沿用所属上下文的 HTTP 503，撤销整批业务、行审计、来源关系与新消息；
Identity 失败时不失效权限缓存，错误密码计数也只有准入后才保存。Scheduling 决定拒绝报告 failed。
既有清理释放额度后可重试；未投递 Memory 数据仍会随进程结束丢失。
真实 broker 旅程仅证明已交付消息在来源结束后仍可被中央接收。完整发布资格及后续治理由 #80 / #64 / #60 跟踪。

### 来源事实容量只读诊断

六个来源模块显式提供 `GET /api/<context>/audit-capacity`，其中 `<context>` 为
`identity`、`platform`、`files`、`scheduling`、`costing` 或 `pricing`。返回沿用统一 `ApiResponse`。
四个平台必须具备对应路径的 GET 权限；根主体仍须通过当前会话校验。
Costing / Pricing 沿用当前根操作者策略，普通业务主体不能读取；这不代表 #54 的普通业务授权已完成。

`context` 说明所属上下文，`isPersistent=false` 表示仅当前进程存活期间保留的 Memory 账本。
`maxRecords`、`maxPayloadBytes`、`maxRecordPayloadBytes` 分别为条数、正文 UTF-8 总字节和单条字节上限；
`retainedRecords`、`retainedPayloadBytes` 为仍保留的占用，包含待投递、死信和确认尚未清理的副本。
`remainingRecords` / `remainingPayloadBytes` 最小为零；`overLimit` 明确标记历史占用超过当前条数或总量策略，
不扫描历史正文推断单条是否超限。Int64 字段按既有 HTTP 契约输出字符串，避免 JavaScript 精度损失。

每个上下文只读取自己的账本。PostgreSQL 在一次查询中读取策略与计数，不扫描 Outbox 正文；
Memory 与准入和清理共用原写锁，争锁时立即报告暂不可读。查询不会改变业务、事实或权限缓存。
HTTP 操作观察仍由现有中间件记录，它与业务提交事实是两类记录。

`<Context>:AuditDelivery:CapacityRead:Timeout` 默认 `00:00:03`，允许 50ms 至 30s，非法值拒绝宿主装配。
这是容量读取适配器连接与查询的独立预算；身份与权限校验仍使用现有安全边界，不能把该数值当成整条 HTTP 链路的截止时间。
读取不可用、账本缺失或超时返回 HTTP 503 / `audit_capacity.unavailable`，不会伪造零占用快照。
此读取接口不修改持久容量策略，也不改变业务写入的锁等待预算；本分支的受权调整由 #101 实施，恢复和中央保留治理由 #64 / #60 继续跟踪。
本轮验收与评审进度见 [#91](https://github.com/yongpengW/NexusStackNext/issues/91)。

### PostgreSQL 来源容量锁等待

六个持久来源使用 `<Context>:AuditDelivery:CapacityWrite:Timeout` 配置触发器内每次容量锁获取的等待上限，默认 `00:00:03`；允许 50ms 至 30s 的整毫秒值，非法配置拒绝启动。由各上下文新增的 `FactCapacityWaitBudget` 迁移接入 V2，升级/降级保留既有事实、策略和占用，历史 V1 不改写。

容量行锁或表锁争用返回 HTTP 503 / `audit_capacity.busy`；额度真正不足仍使用原来各上下文的 exhausted 错误。失败不提交业务及对应的事实、任务或结果消息，也不触发提交后的权限或价格缓存失效；释放锁后可重新提交。已受理的文件删除若无法保存完成事实，继续作为可恢复待办处理。

预算只在事实准入和过期已确认副本清理访问容量账本时生效；不改变普通业务消息、业务表的锁等待或原会话设置，调用者更短的正数锁等待及取消继续有效。它不是整个 HTTP / 事务的时限。原始锁超时在数据库内转为不可自动重试的精确容量错误，避免 EF 自动重试放大争用。清理遇到争用则整批回滚并沿用已有维护重试。

实现依据和边界见 [ADR-0025](adr/0025-context-owned-fact-capacity.md)，PostgreSQL 验收见 [#93](https://github.com/yongpengW/NexusStackNext/issues/93)。策略调整审计、专用恢复与保留治理仍在 #64 / #60 跟踪。

### Memory 来源共用写锁等待

四个平台 Memory 来源复用 `<Context>:AuditDelivery:CapacityWrite:Timeout`，默认三秒，允许 50ms 至 30s 的整毫秒值；无效配置在模块装配时拒绝。预算覆盖共用业务写锁的每次获取，包括提交前读取、业务与事实提交、交付状态、条件人工重试和清理。诊断快照仍立即报告 `audit_capacity.unavailable`，不进入三秒等待。

等待期间调用者取消继续传播；预算耗尽报告 `audit_capacity.busy`，公开 Result 边界保留此错误码，HTTP 返回 503，OpenAPI 声明相同的依赖失败响应。它不是整次请求的截止时间，不强制终止已开始的提交或外部回调，也不遗留仍在运行的后台写入任务。

拒绝不提交半批事实、业务状态或容量，也不在提交前失效权限缓存。已受理删除的完成确认遇到争用时保持待办；上传失败仍遵循写入保护与先退役后回收的孤儿协议。调度登记忙拒绝作为失败计划报告，不能成为合法跳过或另造退避事实。独立清理适配器同样使用有限获取预算，未知异常仍传播并释放作用域。实施与完整验收由 [Memory 共用写锁预算 #96](https://github.com/yongpengW/NexusStackNext/issues/96) 跟踪。

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

### 容量调整事实（#101 实施中）

中央明确注册六个来源 Contracts 的 `fact-capacity-policy-changed.v1` 处理器与 RabbitMQ 订阅，
共用摄入实现在 Platform 和 Identity 两个真实来源分别转绿后收拢。每个闭合类型和来源均由 Auditing 模块声明，
不会从请求或消息的额外字段读取 schema、来源或动作。控制订阅沿用 `Auditing:Messaging:ConsumerName`；
队列名同时包含事件名和消费名，因此六种事件有各自队列与重试/死信路径，既有业务订阅名称保持不变。

调查 `/api/auditing/entries` 可按 `source`、`action=<source>.fact-capacity-policy.changed` 或固定客体
`subjectType=fact-capacity-policy&subjectId=<source>` 过滤。返回的 `fact.capacityPolicyChange` 包含
`requestId`、`policyRevision`、固定 `reason=operator-adjustment`，以及 `previous` / `current` 的
`maxRecords`、`maxPayloadBytes`、`maxRecordPayloadBytes`。Int64 继续以十进制字符串返回。
操作者与执行关联保留在外围 fact，不复制配置值、请求正文或任意理由文本。

数值、请求身份或其他事实内容改变的同身份消息被拒绝，不能覆盖原证据。
普通业务事实的治理值对象为空，继续使用原三种指纹形状；新治理事实把数值加入指纹。
治理事件没有明确前后额度时，领域拒绝保存只写 changed 的占位记录。
`CapacityPolicyAuditEvidence` 正常增量迁移增加自身明确数值列，旧事实与 Inbox 保留；已有治理数值时
回退以 `auditing_fact_policy_history_exists` 拒绝，避免字段删除后调查只剩动作标题。

本分支已完成直接摄入、基线旧指纹升级重放与安全回退的阶段验证，并补六个 PostgreSQL 来源的真实
HTTP → Outbox → RabbitMQ → 中央 typed 查询资格。中央未运行时 broker 保留事件，来源进程退出后
仍能接收，中央 OS 进程重启后六来源证据保留。四个平台 Memory 也验证已发布消息在来源进程退出后可查；
这不承诺未发布的 Memory 消息跨进程恢复，也不是 broker 进程重启资格。

同身份重复交付不新增记录，数值冲突进入死信且不覆盖原证据；数据库异常则保留原消息并重投。
中央数值列被故障触发器清空时，实际 SQL 约束拒绝且 Inbox 回滚，恢复后原消息仍可接收；
真实消费者在 Inbox 登记后、事实插入前被杀掉，重启后也能完整接收。来源凭据经所属公开清理端口
安全删除后，同一 RequestId 的再次调整生成新的 EventId，中央保留两次不同版本的数值变化。

控制凭据至少保留七天，实际交付后的副本还至少保留二十四小时。PostgreSQL 只保存微秒，
因此凭据期限及 EF Outbox 的首次确认时刻采用保守的微秒向上取整，不能因截断提前清理。
重复确认保留第一次确认时刻；Memory 使用原时刻，不降低其精度。六个所属 PG 适配器的
公开确认/清理接口已验证小于一微秒的期限边界和重复确认，既有 V1/V2 与消息载荷未改。
工作区六来源均已接入所属后台控制维护；共同调度在 Platform 与 Identity 两个真实消费者验证后才收拢，
不改变所属清理端口、数据库或 Outbox。配置为 `<Context>:AuditDelivery:PolicyMaintenance`：
`Enabled` 默认 true，`BatchSize` 默认100（1–1000），`Interval` 默认一分钟（1秒–1小时），
`Timeout` 默认3秒（50毫秒–30秒）。这些参数不能缩短七天凭据期或交付后二十四小时期限。
`<context>-policy-cleanup` 使用 `auditing-diagnostics` 标签，报告开关、运行、失败、降级与释放请求数；
计数是进程诊断，重启后重新计数，不作为持久审计证据。维护故障不进入业务就绪检查，下一轮成功解除降级。
四个平台的 Memory 控制池另可在启动时通过 `<Context>:AuditDelivery:MemoryPolicyControl` 缩小额度；
默认1000条/16MiB总量/16KiB单条，三个值均须为正且不超过各自默认值，单条不超过总量。
这组不可变配置不支持在线修改，不影响业务容量PUT，也不覆写PostgreSQL持久账本；
维护关闭时仍验证。字节计量包含真实序列化事实与重放凭据，采用UTF-8字节而非字符数。
无事实的空操作凭据也占用及释放一个名额；未交付和死信不能因维护到期而消失。
关闭维护仍报告明确开关状态；失效配置即使关闭也拒绝。取消会等待所属存储操作结束，不遗留后台写任务。
策略 PUT 的操作观察使用固定 `<context>.fact-capacity-policy.adjust` 和静态说明，不收任意理由、正文、
query 或敏感请求头。平台模块的执行来源仍为 platform，动作标明所属上下文；首次提交事实关联原操作，
重放和拒绝各有独立观察，不据 completed 推断新的策略变更。四平台 Memory / PostgreSQL 与两业务
真实 OS 进程 / PostgreSQL journal 的十项矩阵已通过，具体边界见[操作日志](operation-logging.md#声明固定说明与安全客体)。
整票全量及最终资格仍在 #101 待办，最新阶段证据见[本地开发状态](handoff-2026-10-03.md)。

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

实施中的容量策略协议另由 `PostgresFactCapacityPolicyFailureTests` 验证六个实际来源的十八项故障路径：
凭据持久写失败、实际容量行锁等待中的调用者取消，以及 Outbox 和额度更新已经在事务内执行后的凭据提交前取消。
锁与触发器只在私有临时库安排故障，结果通过所属策略、凭据重放和 Outbox 端口检查；失败不保留部分状态，
解除故障后原请求可提交，重放不重复产生事实。取消必须终止实际写任务，不能只取消调用方等待而丢弃写任务。
可编译的提前提交和忽略取消变异已被这些接口断言拒绝，源码还原后十八项重新通过；
本轮是已有行为的回归资格，不等于六来源全部故障、迁移或权限义务已完成。完整状态仍见 #101。

阶段十四的 `FactCapacityPolicyMigrationTests` / `FactCapacityPolicyBusinessMigrationTests` 共六项，
在真实所属迁移边界验证三个旧额度、实际占用及原业务事实保留，重复迁移后的凭据重放，
以及有治理历史时明确拒绝破坏性 Down。Costing/Pricing 使用升级前后的真实业务宿主进程，
其余四项主要使用同一测试进程内的宿主重建。移除 Down 保护、Up 覆盖旧额度的可编译变异均被拒绝，
源码精确还原后六项和330项相关回归通过；这仍不是整仓全量或 #101 最终发布资格。

阶段十五新增 `PostgresFactCapacityPolicyAccessTests` 的四平台×三份实际网关配置十二项：
普通用户登录、策略读取/调整及注销都经过真实HTTP，验证独立写权限、可信操作者/来源、
精确大整数、拒绝/旧凭据重放拒绝时无策略与控制占用变化，以及业务事实计数/字节隔离。
撤权用现有仓储/提交端口及显式权限缓存刷新安排；不能宣称自动撤权HTTP命令已经实现。
PUT错误使用GET权限的可编译变异被拒绝，源码精确还原后342项相关回归通过，仍非整票最终资格。


阶段十六新增业务网关三项和各来源OpenAPI十项资格：两个业务真实OS来源/PG经实际路由，
验证Root/可信Actor、精确Int64、重放及冲突与业务占用隔离；样板JWT夹具不代表Identity会话撤权集成。
实际文档覆盖四平台Memory/PG及两个业务宿主，检查六输入/八回执字段、数值契约和状态码声明。
移除派生IsValid隐藏的可编译变异被拒绝并精确还原；355项相关回归通过仍非整仓全量或整票发布资格。


阶段十七的十个实际来源协议用例通过公开策略/清理/Outbox端口，验证四平台Memory/PG及两个业务PG的
CAS唯一胜者、ABA/异内容/异Actor拒绝、旧裁决/空操作重放、固定期限和ACK24小时、有限释放与RequestId重用的新事件身份。
调用方丢弃成功端口返回后恢复原裁决，验证层为存储协议；实际TCP断流和业务模块OS启动不由这些用例证明。
Memory去Actor绑定/PG绕过CAS的可编译变异被拒绝并精确还原；365项相关回归各一次通过仍非整仓全量或整票发布资格。

阶段十八的四平台真实崩溃恢复复用原登录会话验证PG策略/占用、原裁决与未发布控制事实身份；
六来源实际PG端口验证最大Int64版本的变化拒绝、空操作保留与次日重放。SQL仅安排边界，
该进程用例关闭发布/维护，证据不跨越到Memory持久化或新增broker资格。
移除PG溢出保护的可编译变异被拒绝并精确还原；410项扩大相关回归各一次通过仍非整仓全量或整票发布资格。

阶段十九的 `FactCapacityPolicyBusinessIsolationTests` 通过实际模块装配和公开业务端口验证三十项：
四平台 Memory/PG 与两业务 PG 共十种装配，在已有两个业务对象和待投递事实的情况下分别降低条数、
总字节及单条额度。原业务对象、版本、任务和事实占用保持；第三个写请求明确背压且无半成品，恢复额度后
原请求可受理。另二十项在策略事实序列化时注入失败或取消，原状态保持，同请求修复后只提交一次。
Files 验证元数据和事实，不将它称为文件物理字节验证；Pricing 的实际 Redis 热缓存资格由独立测试提供。
四次可编译变异分别移除条数/字节超额诊断、丢弃原 Outbox 事实、忽略提交取消，均被接口断言拒绝并精确还原。

## 容量策略验收定位

下表指向 #101 的实际测试面；完整发布资格仍需本机整仓检查、独立双轴评审及当前提交的 Linux CI。

| 义务 | 实际验证入口 |
|---|---|
| 六来源 HTTP、授权、网关与精确契约 | `FactCapacityPolicyAccessTests`、`PostgresFactCapacityPolicyAccessTests`、`BusinessFactCapacityPolicyAccessTests`、`BusinessFactCapacityPolicyGatewayTests`、`FactCapacityPolicyOpenApiTests`、`FactCapacityPolicyOperationTests` |
| 满额扩容、降额保留、准确业务占用 | 六来源 `*FactCapacityPolicyTests`、`FactCapacityPolicyBusinessIsolationTests`；旧/新 typed 数值由 `FactCapacityPolicyAuditIngestionTests` 及真实 broker 调查验证 |
| CAS、ABA、精确重放与有限保留 | `FactCapacityPolicyProtocolTests` 覆盖十种装配；`PostgresFactCapacityPolicyRevisionTests` 覆盖六 PG 溢出；`FactCapacityPolicyProcessRecoveryTests` 覆盖四平台同会话 OS 恢复，业务恢复另由两个样板旅程验证 |
| 独立条数、UTF-8 字节、清理与故障保留 | 两个 `*FactCapacityPolicyControlBudgetTests` 共三十四项；`FactCapacityPolicyDeadlineTests`、`FactCapacityPolicyMaintenanceTests`、`SchedulingFactCapacityPolicyHistoryTests` 及各来源清理用例 |
| 来源 Outbox、真实 RabbitMQ、中央 Inbox 与 typed 调查 | `FactCapacityPolicyBrokerJourneyTests` 共五项：六 PG 来源退出后摄入、四 Memory 已发布证据、离线/崩溃恢复、重复/内容冲突、过期请求重用的新事件身份、Inbox 与记录之间崩溃 |
| 业务隔离与失败原子性 | 新业务隔离三十项、六 PG 持久失败/实际等待取消/提交前取消十八项；Identity 权限缓存、Pricing 实际 Redis 热缓存及 Scheduling 非空历史由各自测试验证 |
| 正常迁移及安全 Down | `FactCapacityPolicyMigrationTests` 与 `FactCapacityPolicyBusinessMigrationTests` 覆盖六来源；`FactCapacityPolicyAuditMigrationTests` 覆盖中央 typed 列升级和 Inbox 指纹保留 |

四平台使用当前会话和资源写权限；两个业务样板按本票决定保持根操作者限制。
Memory 不承诺未发布事实的进程恢复；“响应丢失后重放”目前验证层为公开存储端口，未模拟真实 TCP 断流。
业务死信专用恢复以其后续原生票据交付记录为准；中央保留/归档和遗漏防线仍不能由本表推断完成。
中央有限接纳与容量调查见[中央审计接纳容量](central-audit-capacity.md)；它不授予事实删除资格。
