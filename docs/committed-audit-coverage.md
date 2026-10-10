# 已提交事实覆盖与验收

对应规格 [自动操作日志、可靠交付与已提交事实覆盖](https://github.com/yongpengW/NexusStackNext/issues/60)，
实施票据 [业务事实审计覆盖与调查和容量治理](https://github.com/yongpengW/NexusStackNext/issues/64)。
这里列的是业务提交义务；HTTP / 命令 / 任务的执行观察不能代替其中任何一项。

## 当前保留政策与遗漏检查

用户于2026-10-10确认中央事实及其Inbox/指纹长期保留，暂缓归档删除，先完善遗漏检查。
操作观察继续按最后接收阶段保留30天；普通导出与来源副本清理不授予事实删除资格。
决定见[Auditing ADR-0009](../src/Services/Auditing/docs/adr/0009-facts-remain-online-with-deduplication.md)。

`EntityAuditCoverageTests` 从实际领域程序集枚举聚合，将每个公开入口登记为状态变化或查询/技术生命周期。
新增聚合、方法、同名重载或公共构造入口未登记，以及已删除对象仍有登记，都会使检查失败。
属性与继承的基类方法不属于该入口清单；它不分析方法体，也不能推断新增分支是否应产生事实。

`OperationEndpointInventoryTests` 启动正常平台、Costing、Pricing与网关装配，开启Files生产者及Pricing导出，
从实际路由读取HTTP写入口。每项明确事实验证或具体例外，入口新增/删除须同步审计决定；
同时对全部端点发送HTTP验证操作观察或显式排除。这里的匿名请求多在授权处拒绝，不能证明受权写入产生事实，
内部文件TLS协议仍由 `GeneratedFilesHttpsTests` 验证，业务提交仍由下方行为矩阵验证。
网关转发由实际代理元数据明确标识，不拥有下游提交事实；网关自身的路由管理写入口仍须登记技术配置例外。
没有HTTP方法约束或空方法集合的路由以 `*` 登记，不能默认为GET；观察遍历对它们发送GET和POST，
健康探测及实时协议也明确登记例外，实时协议仍不属于本轮HTTP业务观察验收。
授权/计划预览的查询型POST没有提交事实，
任务、发生、恢复凭据与导出委托使用自身持久生命周期，不冒充成本/报价对象变化。

上述清单是测试侧维护义务，不增加产品元数据或运行期反射。新增业务入口若复用现有领域方法，
仍须补相应行为例子；清单登记、测试名字存在或HTTP返回成功都不能证明同事务事实已经生成。
清单资格见[审计义务清单与新增入口遗漏检查](https://github.com/yongpengW/NexusStackNext/issues/160)。
下面按当前 dev 汇总行为证据；各切片的本机、Linux CI、模板及独立评审记录保留在对应原生票据。
本轮仅补 Files 后台取消回归并核对总体验收，不重复实现已经交付的容量和恢复模块。

中央新增切片[有限接纳与容量诊断](https://github.com/yongpengW/NexusStackNext/issues/156) 的验证定位：
`CentralAuditCapacityTests` 覆盖 Memory 额度与取消；`CentralAuditCapacityPersistenceTests` 覆盖 PG
竞争、回滚、短等待、迁移回填与缺失账本拒绝；`CentralAuditCapacityHttpTests` 覆盖独立授权、
非法配置、诊断故障与业务 ready 隔离；`CentralAuditCapacityBrokerTests` 覆盖真实 broker 保管和容量恢复。
`AuditAccessTests` 使用三份发布路由验证容量诊断的真实 HTTP 转发、认证及网关日志零新增，路由覆盖检查同时拒绝遗漏。
交付资格以原生票据为准。观察保留由[整组清理](https://github.com/yongpengW/NexusStackNext/issues/158)完成，
事实长期保留与遗漏检查由上述用户决定和清单明确；它们不授予事实归档删除资格。

## 覆盖矩阵

| 所有者 / 聚合 | 必须保留的持久变化 | 当前验收状态 |
|---|---|---|
| Platform / GlobalSetting | 创建、值或说明变更、清空值；版本未变不记新事实 | `AuditBusinessJourneyTests` 验证提交、回滚、空操作、来源重启、真实 MQ 和中央库故障；`PlatformFactCapacityTests` / `MemoryPlatformFactCapacityTests` 验证整批准入；策略与条件恢复见下方六来源治理矩阵 |
| Identity / User | 注册、角色分配/撤销、密码/联系方式变更、启禁用、会话撤销、成功登录状态、失败计数/锁定 | `IdentityCommittedFactPersistenceTests` / `MemoryIdentityTransactionTests` 验证实际变化、空操作、失败、取消、竞争与同作用域重试；`IdentityCommittedAuditTests` 验证 HTTP 拒绝仍提交安全变化、来源写失败、真实重启和 MQ |
| Identity / Role | 创建、改名、平台范围、菜单授权/撤销/替换 | PostgreSQL 创建及修改经 `IdentityCommittedFactPersistenceTests` 验证，Memory 经 `MemoryIdentityTransactionTests` 验证；创建/授权经真实消息旅程验证。当前没有删除入口，未来新增删除须连同事实验收 |
| Identity / MenuTree | 建树、增删节点、移动、名称/排序改变 | PostgreSQL 节点差异经存储端口验证，包括删除最后节点、创建时已有节点、移动子树及删后重建的净变化；客体为树根及其版本，关联为节点；建树及节点创建经真实消息旅程验证 |
| Identity / ApiResource | API 资源登记 | PostgreSQL 登记、所属 Menu 与非法输入拒绝经 ISender 验证，登记关联经真实消息旅程验证；Memory 批次失败与净变化由 `MemoryIdentityTransactionTests` 验证。当前没有删除入口，未来新增删除须连同事实验收 |
| Identity / RefreshToken | 签发、消费、撤销；只记录令牌内部标识，禁止令牌及其哈希/自由文本原因 | PostgreSQL 三类变化与重复撤销经存储端口验证；签发/消费、所属 User 关联及重放造成的会话撤销经真实消息旅程验证 |
| Files / StoredFile | 登记、存储完成、请求删除、实际字节移除、清理延期；生成成果候选封存、发布、到期 | `FilesCommittedAuditTests` / `MemoryFilesCommittedAuditTests` 验证 HTTP、空操作、竞争与来源写失败整批回滚；`FileRecoveryOperationTests` 验证存储/提交故障及 PG 提交中取消；`GeneratedFilesHttpsTests` 验证生成成果事实、重放、回滚和重启；生产存储重启及两种来源真实 MQ 交付通过 |
| Scheduling / ScheduledTask | 创建、规则/间隔变化、启禁用、触发推进、跳过/延期裁决、故障清除 | `SchedulingCommittedAuditTests` 验证两种存储及真实重启/MQ；`SchedulingFactAtomicityTests` 验证计划、决定、发生、两类消息整批回滚与同作用域重试；`SchedulingOperationTests` / `CalendarSchedulingPersistenceTests` 验证取消、崩溃及恢复；容量与审计恢复见下方治理矩阵 |
| Costing / CostSheet | 成本输入创建/修改、计算结果实际应用 | `CostingCommittedFactTests` 验证实际变化、拒绝/空操作/过期结果及来源写失败回滚；`CostingFactCompletionTests` 验证保存中取消与租约过期；`CostingCommittedAuditTests` 验证来源重启、真实 MQ 和系统执行关联；`CostingFactCapacityTests` 验证原子容量拒绝与有限任务恢复 |
| Pricing / PriceQuote | 手工输入创建/修改、成本消息实际应用、计算结果实际应用 | `PricingCommittedFactTests` / `PricingFactCompletionTests` 验证来源故障、取消、租约失权与同作用域重试；`TaskOperationJourneyTests` 验证真实重启/MQ 关联；`PricingFactCapacityTests` / `PricingCacheTests` 验证容量拒绝、真实 Redis 故障与持久失效恢复；审计专用恢复见下方治理矩阵 |

菜单节点及授权关联归其聚合根，事实客体版本是根的已提交版本。Auditing 的不可变记录、
Inbox/Outbox、任务租约/尝试和消息接收凭据使用各自的生命周期证据，不对审计写入再递归生成审计。
新增聚合必须登记事实义务或明确的例外；仅有实体行审计字段不代表完整历史已覆盖。

PricingExport 是明确的技术生命周期例外：导出委托保留自身状态/版本、冻结快照与发布裁决，
由 `PricingExportAcceptanceTests` / `PricingExportRecoveryTests` / `PricingExportJourneyTests` 验证。
导出操作被观察，Files另存成果生命周期事实，不伪造报价变化事实。

## 治理与规格验收对照

| 验收义务 | 当前行为证据与交付定位 |
|---|---|
| 全宿主默认观察、可信身份、安全字段和独立执行关联 | [HTTP 覆盖](https://github.com/yongpengW/NexusStackNext/issues/62) 与[命令/后台关联](https://github.com/yongpengW/NexusStackNext/issues/63)；`OperationEndpointInventoryTests`、`ScheduledCostBusinessJourneyTests`、`TaskOperationJourneyTests` 验证实际入口及跨执行关系 |
| 最小事实同事务；失败阻止提交，拒绝/空操作不伪造成功变化 | 上方逐聚合矩阵；错误密码计数是已提交变化，不能以 HTTP rejected 抹掉；来源事实写失败不会提交半成品 |
| 有界关联调查、默认窗口、权限与必要索引 | `AuditInvestigationTests` / `AuditInvestigationIndexTests` / `AuditAccessTests` 验证精确条件、最近七天默认窗口、最多三十一天、授权及正常优化器计划；不读其他上下文的业务表 |
| 来源观察有限积压、超时、死信/重试、已交付清理及可见降级 | `OperationJournalCapacityTests` / `OperationJournalPersistenceTests`、`OperationJournalDeliveryTests`、`OperationJournalRecoveryTests` / `OperationJournalRecoveryCleanupTests` / `OperationJournalRecoveryCommandTests`；`OperationLoggingJourneyTests` 验证中央离线、来源重启与真实 broker 保管 |
| 六来源事实准入、等待预算、诊断及已确认副本清理 | 下方容量测试；[Memory 整批准入](https://github.com/yongpengW/NexusStackNext/issues/80)、[只读诊断](https://github.com/yongpengW/NexusStackNext/issues/91)、[PG 等待](https://github.com/yongpengW/NexusStackNext/issues/93)、[Memory 等待](https://github.com/yongpengW/NexusStackNext/issues/96)；`CommittedFactCleanupTests` / `CommittedFactCleanupHostTests` 与两种 Memory 清理测试验证选择边界、失败回滚和自动维护 |
| 六来源策略调整留痕与隔离 | [策略治理](https://github.com/yongpengW/NexusStackNext/issues/101)；各来源 `*FactCapacityPolicyTests`、`FactCapacityPolicyProtocolTests` / `FactCapacityPolicyTransactionTests`、`PostgresFactCapacityPolicyFailureTests` 验证原子性、重放和取消；`FactCapacityPolicyBrokerJourneyTests` 验证六 PG / 四 Memory 已发布证据、中央离线与崩溃恢复 |
| 六来源审计死信专用条件恢复与有限凭据 | [条件恢复](https://github.com/yongpengW/NexusStackNext/issues/103)；各来源 `*FactDeliveryRecoveryTests` 验证只恢复选定消息且不改业务；`PostgresFactDeliveryRecoveryFailureTests`、`FactDeliveryRecoveryEvidenceTests` 验证失败、取消、不可改写和到期；`FactDeliveryRecoveryTests` / `FactDeliveryRecoveryRestartTests`、`IdentityFactDeliveryRecoveryTests`、`CostingFactRecoveryEvidenceTests` / `PricingFactRecoveryEvidenceTests` 验证真实进程恢复 |
| 中央有限接纳、观察保留与事实保留 | [中央容量](https://github.com/yongpengW/NexusStackNext/issues/156)、[观察清理](https://github.com/yongpengW/NexusStackNext/issues/158)、Auditing ADR-0009；`CentralAuditCapacityBrokerTests` 验证满额保留原消息，`OperationObservationRetentionPersistenceTests` / `OperationObservationRetentionHostTests` 验证整组删除、去重到期和事实保留 |
| 新入口与聚合遗漏检查 | [审计义务清单](https://github.com/yongpengW/NexusStackNext/issues/160)；实际程序集/路由非空、双向清单、可编译反向变异与行为回归；清单局限见前文 |
| 本机、Linux、独立双轴及模板资格 | 各已交付票据保留冻结版本证据；本轮新增回归、矩阵核对和最终合并资格集中记录于[父票](https://github.com/yongpengW/NexusStackNext/issues/64)，通过后再关闭规格 |

这里验证当前已公开业务入口及明确故障边界，不表示所有未来分支或故障组合都被穷举。
Memory 未发布记录不保证跨进程恢复；RabbitMQ 保管不等同于中央落库，也不证明 broker 磁盘容量或多机 HA。
长期保留的中央事实仍受接纳上限保护，容量耗尽需要运维扩容；普通调查导出不提供归档删除资格。
Identity 既有登录/刷新多聚合事务的建模不因日志事实接入而改变。

## PostgreSQL 容量锁等待验收

[#93](https://github.com/yongpengW/NexusStackNext/issues/93) 补充六个来源的 V2 锁等待协议与安全 `audit_capacity.busy`，不修改历史迁移或容量策略。
`FactCapacityAdmissionTests` 覆盖四个平台的回滚/恢复、Identity 提交后权限失效、Files 已受理删除恢复、调度决定/发生原子性、取消及非所属错误不误判。
`BusinessFactCapacityAdmissionTests` 覆盖 Costing/Pricing 的人工输入和同租约结果恢复，以及 Pricing 消费不确认、整批回滚、费率和重复请求语义。
`PricingCacheTests.LedgerContention_PreservesHotCacheAndLeavesNoInvalidationToReplay_AfterRestart` 使用真实 Redis 和进程重启，再使所属临时业务库不可用，验证拒绝没有留下失效待办；成功提交后价格缓存更新。
`FactCapacityWaitProtocolTests` 验证行锁/表锁、调用者更短锁预算、保存点后的整批回滚、原会话设置与普通消息隔离，以及已确认副本清理原子性。
`FactCapacityWaitMigrationTests` 在真实 PostgreSQL 上执行六个公开迁移的 Down/Up 操作，验证额度、UTF-8、保留事实和不可变保护；`FactCapacityWriteConfigurationTests` 验证宿主拒绝无效预算、接受上下界。
`ScheduleCapacityFailureTests` 验证额度耗尽和锁争用均按稳定错误码识别；即使存储适配器使用不同诊断文字，也不能把失败计为合法跳过或追加退避事实。
该协议的合并资格以 #93 为准；其后 Memory 写锁预算、策略、专用恢复与中央保留等交付见上方治理对照。

## 必须分开的结果

- 操作被拒绝不代表没有状态提交：错误密码可能提交失败计数或锁定。事实描述该状态变化，不能写成登录成功。
- 来源业务事务回滚时不能留下成功事实；来源事实写入失败必须阻止提交。
- 中央 Auditing 或 MQ 暂不可用时，来源已提交事实由本上下文 Outbox 保留；不得把中央空查询当作没有发生变化。
- 操作观察的来源 journal 与业务事务独立；它允许可见降级，因此事实不能要求对应观察一定存在。
- 原发起人只用于关联，不冒充当前系统执行者，也不作为权限凭据。

## 逐项验收方式

沿用 HTTP / ISender、存储端口、真实 RabbitMQ、独立迁移和进程重启边界。每个上下文都需要成功、
拒绝/空操作、回滚、来源事实写失败、MQ/中央库故障恢复及重启证据；其中不适用项须说明具体原因。
至少一条真实消息旅程证明最小事实在来源和中央之间完整交付，不能以领域事件存在或 EF 模型含列代替。

有界检索、默认时间窗口及选择性查询的索引使用由 `AuditInvestigationTests` / `AuditInvestigationIndexTests` 验证；
来源容量、死信恢复与保留策略分别按上方治理对照验收，查询计划检查不代表生产容量测评。

来源操作 journal 已增加记录数、总载荷和单条载荷上限；`OperationJournalCapacityTests` 验证
Memory/PostgreSQL 的额度、重复与冲突、被拒绝身份的重试、并发、迁移回填和真实 HTTP 可观测降级。
`OperationJournalPersistenceTests` 以一个可用额度验证阻断写入取消后的恢复。额度限制覆盖普通操作观察，
业务事实 Outbox、中央保留和恢复分别使用独立的验收证据，不能由普通观察额度测试代替。

普通来源观察的已交付副本已有按确认时间的有界清理和原子额度释放；`OperationJournalCleanupTests`
验证保留边界、待投递/死信保留、取消/故障回滚、并发、宿主自动运行及超时恢复。
中央已保存观察、指纹与 Inbox 不受来源清理影响；清理后的重投与冲突仍经中央存储端口验证。
`OperationJournalDeliveryTests` 已验证来源维护端口的安全分页、停止状态与恢复版本条件重试、并发唯一胜者、
取消/保存失败回滚及重建服务后的状态。`OperationLoggingJourneyTests` 的恢复清理旅程使用真实 broker 和中央进程，
验证中央离线时已获确认且来源已清理的消息仍可交付，随后重投不重复登记。
公共 Outbox 的恢复版本已隔离旧批次迟到失败，Memory / PostgreSQL 阻断发布测试验证新预算保持不变，
旧真实确认仍被接受，统计只包含实际生效的失败更新。四宿主只读维护命令已由真实进程验证；
`OperationJournalRecoveryTests` 验证原子恢复凭据、同请求并发重放、异内容冲突、独立额度及源消息清理后的留痕。
`OperationJournalDeliveryTests` 还验证状态和凭据任一写入失败、取消后都不留下部分恢复。
`OperationJournalRecoveryCleanupTests` 验证固定期限、批次、取消/删除失败回滚、并发计数与双存储宿主自动清理，
并验证 PostgreSQL 重启后缩短配置不会缩短已有期限；`OperationJournalRecoveryHealthTests` 验证控制额度满额及清理后的诊断。
`OperationJournalRecoveryCommandTests` 验证四宿主真实进程的条件恢复、系统执行标签、重复重放与凭据查询、
配置的恢复额度与期限，以及过期状态、非法条件和配置错误的安全拒绝。
业务事实 Outbox 的容量、清理和中央保留政策在下面独立验收。

PostgreSQL 事实副本清理已接入六个生产模块：按明确事实事件和首次确认期限选择，批次内原子删除，
不清理业务交付消息、待投递、死信或 Inbox。`CommittedFactCleanupTests` 验证边界、并发、锁等待取消和删除失败；
`CommittedFactCleanupHostTests` 验证六个真实上下文模型的自动维护，其中 Costing/Pricing 通过实际模块装配，
并验证维护故障降级与恢复。对应索引迁移已生成并在临时库执行。
四个平台生产模块的 Memory 模式也已接入同一维护端口和循环；`CommittedFactMemoryCleanupTests` 验证快照替换、
并发、写锁等待取消与选择范围，`MemoryCommittedFactCleanupTests` 验证实际模块的自动/关闭策略、
边界、停止记录保留、迟到应答和业务版本保持。成功轮次及其删除数由两种适配器的宿主测试共同验证。
Platform PostgreSQL 容量准入已通过 `PlatformFactCapacityTests` 验证：并发最后额度、业务回滚、空操作、
安全清理释放额度、UTF-8 单条/总量边界、写入与清理失败的占用回滚、其他消息隔离和固定消息身份。
Identity PostgreSQL 的整批容量、HTTP 拒绝、同作用域重试、失败不失效权限、空操作和错误密码计数
由 HostIntegration / Identity.Integration 两组 `IdentityFactCapacityTests` 验证。
Platform / Identity 的容量模型和冻结的 PostgreSQL 触发器 V1 已提取为共用实现，Files 第三个消费者显式接入。
`FilesFactCapacityTests` 通过 HTTP / 仓储 / Outbox / 清理接口验证上传整批准入、删除拒绝保留下载、
删除已受理后的 202 恢复、并发最后额度、空操作与同作用域重试。升级的策略保留及 UTF-8 占用回填由
`FactCapacityUpgradeTests` 分别验证四个上下文。迁移协议冻结依据见 [ADR-0025](adr/0025-context-owned-fact-capacity.md)。
Scheduling PostgreSQL 也接入同一冻结协议；`SchedulingFactCapacityTests` 验证 HTTP 503、空操作、退避拒绝、
单条载荷上限、并发最后额度、恢复与决定整批回滚、幂等重放、清理释放及真实进程崩溃后重启。
容量拒绝报告 failed，不伪装成合法 skipped；普通发生消息不占事实额度且不被事实清理删除。
Costing PostgreSQL 容量已通过 PR #79 合并；Pricing 容量及新增恢复验收已通过 PR #81 合并，见 #78 的最终资格记录。
四个平台 Memory 对等容量由 #80 验收：各自的 `Memory*FactCapacityTests` 覆盖整批准入、UTF-8 单条和总量边界、
同作用域恢复、权限失效、来源关系及普通消息隔离；`MemoryFactCapacityConcurrencyTests` 覆盖最后额度和清理竞争，
`MemoryFactCapacityConfigurationTests` / `MemoryFactCapacityHttpTests` 验证真实模块装配拒绝与所属 HTTP 503。
既有 Memory 事务/事实故障测试同时使用有限额度验证取消与构造异常不泄漏占用。
共享内存计量只处理新写/删除批次；各上下文独立账本，删除与额度释放共用业务写锁。
Memory 容量的本机检查、Linux CI 与双轴评审以 #80 最终记录为准；策略管理及专门恢复分别由 #101 / #103 交付，中央事实归档按用户决定暂缓。
来源容量只读诊断由 [#91](https://github.com/yongpengW/NexusStackNext/issues/91) 实施，发布资格以该票最终记录为准。
`FactCapacityDiagnosticsTests` / `BusinessFactCapacityDiagnosticsTests` 验证各来源和存储模式的实际占用、剩余量、超限与精确整数。
`FactCapacityAccessTests` 验证四个平台的实际授权与撤回、三个网关配置的真实 HTTP 转发、OpenAPI 及操作观察；业务样板仍只允许根操作者。
`FactCapacityFailureTests` 验证账本缺失、独立短预算、服务器查询结束、调用者取消及数据库不可用恢复；
`MemoryFactCapacityDiagnosticsConcurrencyTests` 验证事实构造期间只返回已提交快照或不可读，恢复后无部分占用。
详见 [ADR-0024](adr/0024-committed-fact-delivery-retention.md)。

Identity 的当前证据为 `IdentityCommittedAuditTests`（HTTP 注册、重复拒绝、错误密码与锁定、来源写失败、进程重启和真实 MQ）
及 `IdentityCommittedFactPersistenceTests`（账户/角色维护、菜单树变化、资源登记、令牌消费/撤销、同作用域保存失败后重试及空保存）。
`PermissionAndTokenFacts_SurviveRestart_AndRetainTheRejectedReplayCommit` 验证一条真实旅程中的 12 条业务事实，
并通过操作标识确认重放同时产生 rejected 观察与 sessions-revoked 事实。现有登录/刷新事务仍有既存的多聚合写入，
增加事实不等于解决该事务建模问题。

User 角色分配/撤销、Role 菜单授权/撤销已逐项携带关联标识，包含聚合创建时已有的关系；
重叠集合替换与提交前删除后原样加回经行为测试证明只记录成员差异。RefreshToken 携带所属 User，
通过关联上下文/类型/标识调查；关联参与内容指纹，不能在重投时替换。

Identity Memory 已接入五类聚合的工作副本、行审计与提交事实；`MemoryIdentityTransactionTests` 验证未提交状态隔离、
普通拒绝和错误密码的不同提交判据、事实批次失败、事务开始前/提交前/事实构造中取消、同作用域重试、版本竞争及集合净变化。
`MemoryIdentityAuditJourneyTests` 验证来源结束后中央仍可从真实 broker 接收已交付的注册和失败登录事实。
未交付的内存记录仍随进程结束丢失，不具备生产来源的重启恢复保证；来源容量、写锁等待与恢复治理见上方对照。
`SuccessfulLoginAtTheSameTimestamp_RecordsTheCommittedFailureReset` 验证相同时间戳下的失败计数清零不会漏记。
菜单及资源关联只带内部标识，不记录原始请求或跨上下文读表补造历史。

Files 的 `FileRecovery.RunOnceAsync` 对每个到期文件建立独立 `recovery` 执行观察；首次删除来源与
删除状态、最小事实同事务保存，后续保存不覆盖来源，原本未知的来源继续保持未知。
`DeferredDeletion_RestartsAsSystemRecovery_AndRetainsTheFirstDeletionOrigin` 通过真实进程重启与 RabbitMQ
验证后台清理事实、独立操作和首次删除请求之间的关联；重复 HTTP 删除不会改写来源，后台 Actor 为空。
`DeletionOrigin_RollsBackWithItsFact_AndOnlyTheSuccessfulFirstDeleteOwnsIt` 验证事实故障回滚也撤销来源写入，
并在同作用域重试、后续保存及并发拒绝后保持第一次成功提交的来源。
Memory 仓储保留相同来源语义，`MemoryFilesCommittedAuditTests` 验证元数据、行审计、首次来源与事实整批提交，
序列化失败/取消不留下部分状态、同作用域重试、跨作用域版本拒绝和系统清理身份。真实 broker 旅程验证来源结束后
中央仍可接收已交付消息；未交付的内存事实仍会随进程结束丢失，不能把此旅程称为 Memory 来源重启恢复。
`FileRecoveryOperationTests` 验证存储故障留下 deferred、重试独立配对，以及移除事实写失败时仅留下
failed 观察而没有完成状态或移除事实；故障恢复后的新操作不会覆盖旧失败证据。
`CanceledRecoveryCommit_PreservesPendingDeletionAndOrigin_AndANewAttemptCommitsOnce` 在 PG 事实写入的容量锁等待中取消：
物理字节已移除，但元数据仍待办、版本/来源/原事实不变，观察为 canceled；下一次独立执行只提交一次完成事实。
它不把文件系统与数据库描述为一个事务；取消后的持久待办负责恢复已经发生的字节删除。

Scheduling 的 `SchedulingCommittedAuditTests` 覆盖两种存储的管理、退避、故障清除与空操作，并通过真实重启 / RabbitMQ
验证中央事实的操作者、当前执行、原发起关系和决定引用。`SchedulingFactAtomicityTests` 对创建、暂停和决定分别
注入来源事实故障，确认计划、决定、发生及两类 Outbox 整体回滚、同作用域重试成功、重复决定和旧版本不追加事实。
`SchedulingOccurrenceTests` 验证竞争扫描仅胜者产生事实，提交失败的 deferred 事实关联本次 failed 观察，成功恢复另建执行。
`SchedulingOperationTests` 的提交取消只保留既有创建事实；`CalendarSchedulingPersistenceTests` 验证决定提交中进程崩溃
不留下决定事实，恢复后的 coalesced / skipped 与故障清除各自只提交一次。中央固定动作及引用组合由
`SchedulingFactIngestionTests` 验证，重复内容保持幂等，变更客体、决定或执行关联拒绝接纳。
PostgreSQL 与 Memory 来源事实容量验证见前文；审计死信专门恢复由 `SchedulingFactDeliveryRecoveryTests` 验证，不改计划及非空历史。
已确认副本清理见前文，Occurrence 的业务重试端口明确拒绝审计消息。

Costing 的 `CostingCommittedFactTests` 验证输入创建/修改与结果应用、重复/冲突请求、旧版本、过期任务及相同结果，
并对创建、更新、任务完成三条路径注入来源事实写失败，确认状态、任务和两类 Outbox 同事务回滚且同作用域可重试。
`CostingFactCompletionTests` 在实际保存中阻塞事实写入，分别取消请求和等待租约到期，确认未提交结果与消息全部撤销，
下一轮租约可以接管完成。`CostingCommittedAuditTests` 验证离线受理后生产者重启，后台完成经 RabbitMQ 交付中央，
事实保留当前操作、原请求、correlation 与原发起人，后台 Actor 为空，不包含金额或请求伪造的 Actor。
`CostingFactIngestionTests` 验证固定动作、信封身份与内容校验、精确调查和不可变重投；未知执行关联不能事后补造。
CostDelivery 查询与重试拒绝审计消息。共享保存生命周期由 Identity 和 Costing 两个适配器使用，
`SerializationInterruptedMidBatch_CommitsNothing_AndSameScopeRetryDoesNotDuplicateFacts` 验证批次构造中断后的原子性。
PostgreSQL 来源容量已实现；`CostingFactCapacityTests` 验证输入/结果/任务/消息原子拒绝、UTF-8 历史回填、单条/总量边界、
最后额度竞争、过期确认清理及真实宿主重启后的有限重试/人工恢复。计划消息受理既有成本快照不消耗新事实额度。
`CostingFactCapacityObservationTests` 验证容量拒绝记 failed，恢复成功是另一次 completed 操作，并保留原发起关联。
容量发布资格见 #76；专门审计恢复由 `CostingFactDeliveryRecoveryTests` 验证，不改已完成任务、计算结果与业务交付。
恢复版本及清理索引已有 Costing 独立迁移并在临时库验收。

`ScheduledCostMessageOperationTests` 验证计划消息消费的独立操作、并发去重、稳定拒绝及失败/取消回滚。
拒绝回执也须提交后才确认消息；任务只关联首次成功受理，重投和重启不覆盖来源，旧消息不从委托人补造执行身份。
`ScheduledCostBusinessJourneyTests` 在真实 broker 和进程中验证：提交前崩溃保留 unconfirmed，恢复后产生新的 accepted；
区间/日历计划在创建人注销后仍保留定义请求、调度、Costing 消费、成本计算与 Pricing 消费的关联。
旅程同时发现并修复新 HTTP 观察遗漏自身根字段的问题，根调查不再漏掉原请求；历史记录保持原样。

Pricing 的 `PricingCommittedFactTests` 验证创建、输入变更、成本版本接纳、结果应用与来源写失败整体回滚；
`PricingFactCompletionTests` 验证保存中取消及租约过期。`PricingMessageOperationTests` 验证独立消息操作、
重复/旧版/冲突消息、失败/取消后重投，以及系统身份不会受调用链中用户影响；首次任务来源跨重启保持不变。
`TaskOperationJourneyTests` 的真实 HTTP / RabbitMQ / PostgreSQL 重启旅程验证五个操作与三条 Pricing 事实，
成本接纳事实引用 Costing 对象，计算事实关联当前后台操作，均保留原始请求且不传金额。
`PricingFactIngestionTests` 验证固定动作、引用约束、信封身份和不可变重投。恢复版本及清理索引已有 Pricing 独立迁移并在临时库验收，
新增 `PricingFactCapacityTests` 验证容量整批拒绝、升级 UTF-8 回填与策略保留、额度竞争、清理恢复、
HTTP 503、worker 有限重试/人工恢复及真实 broker 死信保留/重启重驱。
`PricingFactCapacityObservationTests` 验证 failed 与恢复操作各自独立；`PricingCacheTests` 验证拒绝保留热缓存、
Redis 故障期间成功提交的持久失效与重启恢复。#78 的完整检查与双轴评审已通过，PR #81 已合并；
`PricingFactDeliveryRecoveryTests` 验证专门审计恢复不改报价及已完成任务，完整治理证据见上方对照。
