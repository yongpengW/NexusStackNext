# 本机并行类的资源审查

## 当前票据124：13个独占类的普通准备

以dev 0620d42完整1935项报告为基线，本轮不增加并行类、不改权重：111类857项普通／240项独占。
新增costing-only、pricing-only迁移蓝图不含operation_journal，避免用含journal的结构掩盖首次日志迁移。
原四种蓝图仍保留；所有模板惰性建立、不可连接，每例独立复制可写库，六种模板由collection清理。
建库、模板迁移、复制和删库仍持有跨进程单许可。以下37项整类继续独占：

| 类（同名源码） | 原项数 | 复用准备与保留边界 |
|---|---:|---|
| FactCapacityPolicyMigrationTests | 4 | 初始平台复制库；后续历史IMigrator升降级、结构与证据断言保持。 |
| FactCapacityPolicyAuditMigrationTests | 1 | 初始平台复制库；历史审计迁移与数据保留断言保持。 |
| FactRecoveryRollbackConcurrencyTests | 6 | 按来源选择平台或业务-only复制库；历史回滚竞争、真实业务进程及等待预算保持。 |
| BusinessFactCapacityDiagnosticsTests | 4 | 普通读取用业务-only复制库；缺ledger故障只改本例。预算启动拒绝方法仍在同一新库实际迁移Costing和Pricing。 |
| FactDeliveryRecoveryTests | 9 | 仅历史方法的普通初始平台准备改为复制；未迁移库、IMigrator及实际进程重启保持。 |
| MemoryFilesCommittedAuditTests | 4 | 中央平台复制库；实际中央进程、Memory来源factory、消息与文件根仍属于本例。 |
| CostingCommittedAuditTests | 1 | 中央平台与来源Costing-only复制库；在尚无journal的来源上实际运行首次journal迁移CLI，生产者重启保持。 |
| CostingFactCapacityPolicyTests | 1 | Costing-only复制库；实际业务进程重启、成本/任务/配额与事件断言保持。 |
| PricingFactCapacityPolicyTests | 1 | Pricing-only复制库；实际业务进程重启、报价/任务/配额与事件断言保持。 |
| CostingFactDeliveryRecoveryTests | 2 | Costing-only复制库；真实进程、ISender、投递恢复和业务结果保持。 |
| PricingFactDeliveryRecoveryTests | 2 | Pricing-only复制库；真实进程、ISender、投递恢复和业务结果保持。 |
| CostingFactRecoveryEvidenceTests | 1 | Costing-only复制库；历史IMigrator两方向、重启、原数据与改写拒绝保持。 |
| PricingFactRecoveryEvidenceTests | 1 | Pricing-only复制库；历史IMigrator两方向、重启、原数据与改写拒绝保持。 |

FactCapacityPolicyBusinessMigrationTests的预算内初始迁移、FactCapacityUpgradeTests的指定旧版本起步、
AuditInvestigationIndexTests与OperationObservationPersistenceTests的实际迁移CLI、Scheduling-only迁移，
以及身份/日历等生命周期旅程均不替换。PricingMessageOperationTests的普通并行范围及既有准备也不改。
全部原测试名、Theory、Assert与故障预算对照保持，临时阶段测量已移除；最终定向与完整资格见
[票据124](https://github.com/yongpengW/NexusStackNext/issues/124)，下方是前轮历史。
37项原身份定向各一次通过，仍在独占worker内执行；558条原Assert及迁移/重启/预算对照未变。
控制器269.7秒、所有权idle、峰值32/100，无压力停止信号；八护栏和重命名模板构建/进度检查通过。

冻结实现本机build → 原1935项完整tests → format全零，23工程原身份各一次Passed、宿主1097项完整，五worker正常返回、所有权idle。965执行源与私有配置不变；八护栏和重命名模板通过。整套59.48 → 53.06 分钟（观察少10.8%）、宿主49.41 → 43.55 分钟（少11.9%），同240项独占37.98 → 33.59 分钟（少11.5%）。峰值48/100、PG最慢1545 毫秒，无压力停止信号；单次环境观察不证明全部收益来自本次改造。双轴、Linux四报告Verify/统一门禁与dev实际资格树见票据124原生交付。

## 已交付票据122：剩余普通资源与独占准备

基线为已合并dev bdfe19f的完整1097项宿主报告。新增7类29项，候选共111类857项；240项仍独占。
所有权、4/2/1路、重操作单许可、整类及Theory同worker和worker内部串行保持。
资格、实测与交付见[票据122](https://github.com/yongpengW/NexusStackNext/issues/122)；以下118/120为历史。

| 新增普通整类 | 项数 | 所属资源与保留边界 |
|---|---:|---|
| OperationJournalCleanupTests | 16 | 每例journal复制库、provider/clock/行锁/故障触发器；真实HTTP宿主动态端口。中央Auditing仍在本例库实际执行迁移CLI，受单许可保护；清理和取消预算不变。该CLI用于准备中央存储，没有删除CLI断言。 |
| OperationJournalPersistenceTests | 3 | 每例journal复制库、provider与行锁；真实等待证明和取消预算保留，TransactionScope属于本例。provider重开不是OS进程重启。 |
| PricingMessageOperationTests | 3 | Pricing-only新库与实际迁移受单许可；自己的provider/消息/触发器/advisory锁640066，整类串行；失败及取消后重投与provider重开保留，不添加journal schema。 |
| PlatformFactCapacityTests | 3 | 前两项Platform-only新库实际迁移受单许可；第三项平台复制库。故障、配额、清理与触发器只改本例库。 |
| SchedulingFactAtomicityTests | 2 | 自有Memory存储或平台复制库、serializer/provider/clock与故障触发器；计划决定与发生原子性断言不变。 |
| GatewayHubTests | 1 | 类所属TestServer、Memory journal、受控健康HTTP适配器、Hub客户端和channel；路由文件只读，变化推送/不变静默的原观察时间保留。 |
| OperationEndpointInventoryTests | 1 | Costing/Pricing-only新库，两个实际迁移在同一重操作许可内；Memory平台factory持有文件根，所属业务factory、动态网关backend与路由文件；真实端点逐个HTTP采集保留。 |

七类继续独占，仅复用普通准备：AuditPersistenceJourneyTests、SchedulingPersistenceJourneyTests、
FactDeliveryRecoveryRestartTests、FactCapacityPolicyProcessRecoveryTests、FactDeliveryRecoveryEvidenceTests、
OperationJournalCapacityTests、OperationJournalCommandTests。Audit/Scheduling独立未迁移库、实际迁移CLI、
历史IMigrator升级/降级及真实进程崩溃/重启不改。JournalCapacity的历史迁移方法和JournalCommand的
未迁移拒绝仍使用新库；FactDeliveryRecoveryEvidence仅换初始平台准备，历史迁移、数据/凭据检查保留。
不修改原方法名、Theory、业务断言、故障预算或产品代码。118阶段对Cleanup的独占记录已由本次具体资源审查更新。

网关原实时通道沿真实runner公开CLI验证：原用例通过但调度先红（parallel=0/exclusive=1），
声明后转绿（parallel=1/exclusive=0），身份保持。修正后66项与冻结实现最终1935项原身份各一次Passed；build/tests/format、八护栏和重命名模板通过，
不将声明清单、筛选结果或未完成运行当成完整交付。

### 定向失败与锁证明修正

首轮66项定向在新增普通日志写入的锁证明处失败，worker正常返回、所有权idle，无压力信号；
该轮不是资格。单个原用例提前读取活动统计后，旧探针在10秒证明预算内失败。
同一事务的pg_stat_activity活动快照会保持不变，无法证明后来出现的阻塞；
现在从pg_locks读取本例数据库、operation_journal.outbox表的未授予锁，并用原pg_blocking_pids
条件确认阻塞者。日志清理的相同探针一并修正。提前读取统计保留为回归准备，
原10秒等待、250毫秒取消、5秒返回/恢复及原断言保持。单个原用例修正后通过，
不把该项通过或前轮片段拼成完整资格；产品代码不变。修正后新一轮66项原身份各一次Passed，控制器323.8秒；29项并行与37项独占完整返回，所有权idle、峰值22/100、无压力信号。冻结实现完整1935项资格也已通过，宿主1097项含857并行/240独占，五个worker正常返回；965执行源和私有配置不变、所有权idle，峰值49/100、PG探测最慢3017毫秒，无连续压力停止信号。单次观察不能证明服务器全部健康。
依据：[事务内活动快照](https://www.postgresql.org/docs/17/monitoring-stats.html)、
[实时锁与阻塞者](https://www.postgresql.org/docs/17/view-pg-locks.html)。

票据118相对dev fcee91c扩大显式清单。以下是具体类与资源归属的审查记录，
分类用于说明代码路径，运行时只读取明确类名；不按正则或名称自动批准新增类。
候选87类770项，327项仍独占；最终资格与交付见[扩大宿主并行与初始化复用](https://github.com/yongpengW/NexusStackNext/issues/118)。

## 票据120新增资源审查

以已合并dev `4f3032e`的完整1097项报告为基线，再加入以下17类58项：
显式清单共104类828项，269项仍独占。类与全部Theory行分配到同一worker，
worker内部串行；所有104类的权重来自同一份完整基线报告。
这是候选范围，最终本机、双轴评审、Linux四报告与交付状态见
[票据120](https://github.com/yongpengW/NexusStackNext/issues/120)，不能以声明清单替代通过证据。

| 类（同名源码） | 项数 | 具体资源与故障边界 |
|---|---:|---|
| [SchedulingOccurrenceTests](../tests/HostIntegration.Tests/SchedulingOccurrenceTests.cs) | 3 | 每例独立平台复制库、HTTP factory、时钟；竞争扫描和失败约束仅作用于本库，后台扫描关闭。 |
| [FactCapacityFailureTests](../tests/HostIntegration.Tests/FactCapacityFailureTests.cs) | 5 | 身份库与来源库各自持有；来源专用Platform schema现场迁移受单许可保护；可用性、250ms故障预算、50ms取消和两秒上限保留。 |
| [FactCapacityPolicyAuditIngestionTests](../tests/HostIntegration.Tests/FactCapacityPolicyAuditIngestionTests.cs) | 9 | Memory中央或独立平台库；业务源进程只启动一次，自己的Costing/Pricing复制库；factory重建及消息去重不依赖另一用例。 |
| [ScheduledCostMessageOperationTests](../tests/HostIntegration.Tests/ScheduledCostMessageOperationTests.cs) | 5 | 自有Costing库与provider；故障触发器、advisory锁640067及取消只在本库，整类串行；重建provider不是OS进程重启。 |
| [BusinessFactCapacityPolicyGatewayTests](../tests/HostIntegration.Tests/BusinessFactCapacityPolicyGatewayTests.cs) | 3 | 独立业务复制库、一次业务进程、动态监听地址；网关所属GUID路由文件，源路由文件只读。 |
| [BusinessFactCapacityPolicyAccessTests](../tests/HostIntegration.Tests/BusinessFactCapacityPolicyAccessTests.cs) | 2 | 自有业务库、一次动态端口业务进程；HTTP身份和读取provider均属于本例。 |
| [OperationJournalRecoveryTests](../tests/HostIntegration.Tests/OperationJournalRecoveryTests.cs) | 4 | 自有journal复制库或Memory存储；并发恢复配额竞争只在本例provider；重开provider读取同一所属库。 |
| [OperationJournalRecoveryHealthTests](../tests/HostIntegration.Tests/OperationJournalRecoveryHealthTests.cs) | 2 | 所属journal库、MutableClock与health服务；清理和配额恢复不共享状态。 |
| [HttpInt64ContractTests](../tests/HostIntegration.Tests/HttpInt64ContractTests.cs) | 5 | Memory factory或本例动态端口WebApplication；Node子进程只通过stdin接收本例JSON，不写共享文件，原20秒预算保留。 |
| [IdentityApiTests](../tests/HostIntegration.Tests/IdentityApiTests.cs) | 8 | 整类持有Memory PlatformApp，默认文件根归factory所有；HTTP请求、令牌与账号不跨worker共享。 |
| [AuthorizationChainJourneyTests](../tests/HostIntegration.Tests/AuthorizationChainJourneyTests.cs) | 2 | 整类持有Memory根账号factory；原两项授权与幂等断言保留，同worker执行。 |
| [CostingRecoveryMaintenanceTests](../tests/HostIntegration.Tests/CostingRecoveryMaintenanceTests.cs) | 1 | 独立Costing-only库现场迁移受单许可保护；所属动态端口WebApplication和MutableClock，原故障行锁及维护预算保留。 |
| [PricingRecoveryMaintenanceTests](../tests/HostIntegration.Tests/PricingRecoveryMaintenanceTests.cs) | 1 | 独立Pricing-only库现场迁移受单许可保护；所属动态端口WebApplication和MutableClock，原故障行锁及维护预算保留。 |
| [CostingFactCapacityObservationTests](../tests/HostIntegration.Tests/CostingFactCapacityObservationTests.cs) | 1 | 所属Costing复制库与provider；配额拒绝、同租约恢复和日志关联只改本库。 |
| [PricingFactCapacityObservationTests](../tests/HostIntegration.Tests/PricingFactCapacityObservationTests.cs) | 2 | 所属Pricing复制库与provider；消息去重、配额拒绝与任务租约恢复均为本例状态。 |
| [CommittedFactCleanupHostTests](../tests/HostIntegration.Tests/CommittedFactCleanupHostTests.cs) | 3 | 所属新库、单上下文实际迁移与索引断言保留，迁移受单许可保护；清理worker、时钟与故障触发器只在本例。 |
| [FactCapacityWaitProtocolTests](../tests/HostIntegration.Tests/FactCapacityWaitProtocolTests.cs) | 2 | 所属Platform-only库实际迁移受单许可保护；原会话配置、锁、事务回滚及等待预算保留，许可在故障阶段前释放。 |

另外九个类只复用普通准备，整类继续独占：FactCapacityPolicyBrokerJourneyTests、
FactDeliveryRecoveryTests、IdentityFactDeliveryRecoveryTests、OperationLoggingJourneyTests、
OperationCoverageJourneyTests、SchedulingDeliveryJourneyTests、SchedulingCommittedAuditTests、
TaskOperationJourneyTests、SchedulingFactCapacityTests。真实Broker、进程退出／崩溃／重启、
历史迁移与回滚断言保留。Scheduling-only及Auditing-only专用建库保留；
日志迁移CLI首次创建存储所需的业务-only库也保留，不能用含journal的模板遮蔽首次迁移。

CalendarRuntimeTests的启动拒绝、GatewayRouteAdminTests的进程环境变量改写、
OperationCompatibilityTests／CommittedFactCompatibilityTests的历史迁移，及
CostingFactCapacityPolicyTests／PricingFactCapacityPolicyTests的真实重启均仍独占。
未审核类继续独占，不凭名称、正则或普通方法的占比自动批准整类。

新增8项Identity调度的公开CLI断言先红后绿，随后新增范围与九个独占类涉及的102项原身份
定向回归全部通过。冻结实现的最终build → 完整tests → format均返回零；
23工程原1935项各一次通过、1097项宿主清单精确相同，包含原日历提交崩溃／重启旅程。
全部worker正常返回、所有权idle，965份执行源与私有测试配置未变；八护栏与重命名模板通过。
资源采样峰值51/100、数据库探测最慢1081.9毫秒，无压力停止信号；结束时连接10。
相同边界整套67.97 → 63.79分钟、宿主58.59 → 53.84分钟；一次观察不排除环境波动。
双轴评审与Linux完整四报告Verify、统一门禁、实际dev交付树以票据120最新原生记录为准。

## 共同资源边界

- **M**：实例所属的内存服务、存储、时钟与故障替身；进程间不共享静态状态。HTTP测试使用所属factory，PlatformApp与PersistentIdentityApp的默认文件根由JourneyFileStorage逐factory持有并清理；显式文件根由调用旅程持有，GatewayHttpApp拥有GUID路由文件和动态端口；文件访问使用所属文件fixture。
- **P**：现有JourneyDatabaseTemplates复制的独立平台库；每例provider、clock、锁与故障只作用于自己的库。冻结模板不含业务数据。
- **C**：普通平台／Costing／Pricing／journal旅程使用所属上下文的空结构复制；BusinessProcess只启动新进程，使用动态端口、所属库、自己的签名配置与正常Dispose；Files路径使用所属GUID目录。
- **B**：前轮已批准类，沿用其逐例数据库、HTTP宿主、文件和故障清理边界。

建库、复制、模板迁移、删库及数据库可用性操作继续受同一跨进程许可约束；
表内的业务行锁、查询取消或故障触发只作用于自己的库，不互相借用数据库或权限。
并行worker内仍串行；Theory参数同行，异常返回继续保留所有权恢复拒绝。

## 显式清单

| 类（tests/HostIntegration.Tests同名源码） | 项数 | 边界 |
|---|---:|---|
| [ApiResponseContractTests](../tests/HostIntegration.Tests/ApiResponseContractTests.cs) | 19 | M |
| [ApiTransportTests](../tests/HostIntegration.Tests/ApiTransportTests.cs) | 3 | M |
| [AuditAccessTests](../tests/HostIntegration.Tests/AuditAccessTests.cs) | 7 | M |
| [AuditInvestigationTests](../tests/HostIntegration.Tests/AuditInvestigationTests.cs) | 6 | B |
| [BusinessFactCapacityAdmissionTests](../tests/HostIntegration.Tests/BusinessFactCapacityAdmissionTests.cs) | 3 | C |
| [BusinessFactDeliveryRecoveryGatewayTests](../tests/HostIntegration.Tests/BusinessFactDeliveryRecoveryGatewayTests.cs) | 3 | C |
| [BusinessJourneyIsolationTests](../tests/HostIntegration.Tests/BusinessJourneyIsolationTests.cs) | 2 | B |
| [CalendarSchedulingTests](../tests/HostIntegration.Tests/CalendarSchedulingTests.cs) | 9 | M |
| [ClaimsCurrentUserTests](../tests/HostIntegration.Tests/ClaimsCurrentUserTests.cs) | 1 | M |
| [CommandOperationTests](../tests/HostIntegration.Tests/CommandOperationTests.cs) | 15 | M |
| [ContextAuthorizationTests](../tests/HostIntegration.Tests/ContextAuthorizationTests.cs) | 2 | M |
| [FactCapacityAccessTests](../tests/HostIntegration.Tests/FactCapacityAccessTests.cs) | 8 | M |
| [FactCapacityAdmissionTests](../tests/HostIntegration.Tests/FactCapacityAdmissionTests.cs) | 7 | P |
| [FactCapacityDiagnosticsTests](../tests/HostIntegration.Tests/FactCapacityDiagnosticsTests.cs) | 13 | B |
| [FactCapacityPolicyAccessTests](../tests/HostIntegration.Tests/FactCapacityPolicyAccessTests.cs) | 12 | M |
| [FactCapacityPolicyBusinessIsolationTests](../tests/HostIntegration.Tests/FactCapacityPolicyBusinessIsolationTests.cs) | 30 | B |
| [FactCapacityPolicyControlBudgetTests](../tests/HostIntegration.Tests/FactCapacityPolicyControlBudgetTests.cs) | 16 | M |
| [FactCapacityPolicyDeadlineTests](../tests/HostIntegration.Tests/FactCapacityPolicyDeadlineTests.cs) | 3 | B |
| [FactCapacityPolicyMaintenanceTests](../tests/HostIntegration.Tests/FactCapacityPolicyMaintenanceTests.cs) | 22 | P |
| [FactCapacityPolicyOpenApiTests](../tests/HostIntegration.Tests/FactCapacityPolicyOpenApiTests.cs) | 10 | C |
| [FactCapacityPolicyOperationTests](../tests/HostIntegration.Tests/FactCapacityPolicyOperationTests.cs) | 10 | C |
| [FactCapacityPolicyProtocolTests](../tests/HostIntegration.Tests/FactCapacityPolicyProtocolTests.cs) | 10 | B |
| [FactCapacityPolicyTests](../tests/HostIntegration.Tests/FactCapacityPolicyTests.cs) | 12 | P |
| [FactCapacityPolicyTransactionTests](../tests/HostIntegration.Tests/FactCapacityPolicyTransactionTests.cs) | 1 | B |
| [FactDeliveryInvestigationTests](../tests/HostIntegration.Tests/FactDeliveryInvestigationTests.cs) | 3 | B |
| [FactDeliveryRecoveryCapacityTests](../tests/HostIntegration.Tests/FactDeliveryRecoveryCapacityTests.cs) | 2 | M |
| [FactDeliveryRecoveryGatewayAccessTests](../tests/HostIntegration.Tests/FactDeliveryRecoveryGatewayAccessTests.cs) | 24 | B |
| [FactDeliveryRecoveryMaintenanceTests](../tests/HostIntegration.Tests/FactDeliveryRecoveryMaintenanceTests.cs) | 2 | P |
| [FactDeliveryRecoveryOpenApiTests](../tests/HostIntegration.Tests/FactDeliveryRecoveryOpenApiTests.cs) | 10 | C |
| [FactDeliveryRecoveryOperationTests](../tests/HostIntegration.Tests/FactDeliveryRecoveryOperationTests.cs) | 10 | C |
| [FactRecoveryCapacityBoundaryTests](../tests/HostIntegration.Tests/FactRecoveryCapacityBoundaryTests.cs) | 20 | C |
| [FactRecoveryPendingWorkTests](../tests/HostIntegration.Tests/FactRecoveryPendingWorkTests.cs) | 8 | C |
| [FactRecoveryPersistenceFailureTests](../tests/HostIntegration.Tests/FactRecoveryPersistenceFailureTests.cs) | 6 | C |
| [FactRecoveryProtocolTests](../tests/HostIntegration.Tests/FactRecoveryProtocolTests.cs) | 10 | C |
| [FileRecoveryOperationTests](../tests/HostIntegration.Tests/FileRecoveryOperationTests.cs) | 2 | P |
| [FilesFactCapacityPolicyTests](../tests/HostIntegration.Tests/FilesFactCapacityPolicyTests.cs) | 7 | C |
| [FilesFactCapacityTests](../tests/HostIntegration.Tests/FilesFactCapacityTests.cs) | 3 | C |
| [FilesFactDeliveryRecoveryTests](../tests/HostIntegration.Tests/FilesFactDeliveryRecoveryTests.cs) | 4 | B |
| [FilesRecoveryMaintenanceTests](../tests/HostIntegration.Tests/FilesRecoveryMaintenanceTests.cs) | 2 | P |
| [GatewayAuthTests](../tests/HostIntegration.Tests/GatewayAuthTests.cs) | 4 | M |
| [GatewayConfigurationTests](../tests/HostIntegration.Tests/GatewayConfigurationTests.cs) | 21 | M |
| [GatewayResilienceTests](../tests/HostIntegration.Tests/GatewayResilienceTests.cs) | 9 | M |
| [HttpInt64OpenApiTests](../tests/HostIntegration.Tests/HttpInt64OpenApiTests.cs) | 1 | M |
| [IdentityDeliveryInvestigationTests](../tests/HostIntegration.Tests/IdentityDeliveryInvestigationTests.cs) | 7 | B |
| [IdentityFactCapacityPolicyTests](../tests/HostIntegration.Tests/IdentityFactCapacityPolicyTests.cs) | 7 | P |
| [IdentityFactCapacityTests](../tests/HostIntegration.Tests/IdentityFactCapacityTests.cs) | 1 | B |
| [IdentityRecoveryControlTests](../tests/HostIntegration.Tests/IdentityRecoveryControlTests.cs) | 1 | M |
| [IdentityRecoveryMaintenanceTests](../tests/HostIntegration.Tests/IdentityRecoveryMaintenanceTests.cs) | 2 | P |
| [IdentityTransactionTests](../tests/HostIntegration.Tests/IdentityTransactionTests.cs) | 1 | M |
| [MemoryCommittedFactCleanupTests](../tests/HostIntegration.Tests/MemoryCommittedFactCleanupTests.cs) | 5 | M |
| [MemoryFactCapacityConcurrencyTests](../tests/HostIntegration.Tests/MemoryFactCapacityConcurrencyTests.cs) | 8 | M |
| [MemoryFactCapacityConfigurationTests](../tests/HostIntegration.Tests/MemoryFactCapacityConfigurationTests.cs) | 44 | M |
| [MemoryFactCapacityDiagnosticsConcurrencyTests](../tests/HostIntegration.Tests/MemoryFactCapacityDiagnosticsConcurrencyTests.cs) | 4 | M |
| [MemoryFactCapacityHttpTests](../tests/HostIntegration.Tests/MemoryFactCapacityHttpTests.cs) | 4 | M |
| [MemoryFactMaintenanceBudgetTests](../tests/HostIntegration.Tests/MemoryFactMaintenanceBudgetTests.cs) | 56 | M |
| [MemoryFactRecoveryFailureTests](../tests/HostIntegration.Tests/MemoryFactRecoveryFailureTests.cs) | 8 | M |
| [MemoryFactWriteBudgetTests](../tests/HostIntegration.Tests/MemoryFactWriteBudgetTests.cs) | 21 | M |
| [MemoryFileRecoveryBudgetTests](../tests/HostIntegration.Tests/MemoryFileRecoveryBudgetTests.cs) | 6 | M |
| [MemoryFilesFactCapacityTests](../tests/HostIntegration.Tests/MemoryFilesFactCapacityTests.cs) | 2 | M |
| [MemoryIdentityFactCapacityTests](../tests/HostIntegration.Tests/MemoryIdentityFactCapacityTests.cs) | 4 | M |
| [MemoryIdentityQueryFailureTests](../tests/HostIntegration.Tests/MemoryIdentityQueryFailureTests.cs) | 3 | M |
| [MemoryIdentityTransactionTests](../tests/HostIntegration.Tests/MemoryIdentityTransactionTests.cs) | 17 | M |
| [MemoryManualRetryBudgetTests](../tests/HostIntegration.Tests/MemoryManualRetryBudgetTests.cs) | 4 | M |
| [MemoryPlatformFactCapacityTests](../tests/HostIntegration.Tests/MemoryPlatformFactCapacityTests.cs) | 6 | M |
| [MemorySchedulingFactCapacityTests](../tests/HostIntegration.Tests/MemorySchedulingFactCapacityTests.cs) | 6 | M |
| [MemorySchedulingRunnerBudgetTests](../tests/HostIntegration.Tests/MemorySchedulingRunnerBudgetTests.cs) | 8 | M |
| [OperationGatewaySemanticsTests](../tests/HostIntegration.Tests/OperationGatewaySemanticsTests.cs) | 12 | M |
| [OperationJournalDeliveryTests](../tests/HostIntegration.Tests/OperationJournalDeliveryTests.cs) | 10 | C |
| [OperationJournalRecoveryCleanupTests](../tests/HostIntegration.Tests/OperationJournalRecoveryCleanupTests.cs) | 6 | C |
| [OperationJournalRecoveryCommandTests](../tests/HostIntegration.Tests/OperationJournalRecoveryCommandTests.cs) | 10 | C |
| [OperationLoggingPipelineTests](../tests/HostIntegration.Tests/OperationLoggingPipelineTests.cs) | 18 | M |
| [PlatformJourneyIsolationTests](../tests/HostIntegration.Tests/PlatformJourneyIsolationTests.cs) | 1 | B |
| [PlatformSettingsAccessTests](../tests/HostIntegration.Tests/PlatformSettingsAccessTests.cs) | 9 | B |
| [PostgresFactCapacityPolicyAccessTests](../tests/HostIntegration.Tests/PostgresFactCapacityPolicyAccessTests.cs) | 12 | B |
| [PostgresFactCapacityPolicyControlBudgetTests](../tests/HostIntegration.Tests/PostgresFactCapacityPolicyControlBudgetTests.cs) | 18 | P |
| [PostgresFactCapacityPolicyFailureTests](../tests/HostIntegration.Tests/PostgresFactCapacityPolicyFailureTests.cs) | 18 | P |
| [PostgresFactCapacityPolicyRevisionTests](../tests/HostIntegration.Tests/PostgresFactCapacityPolicyRevisionTests.cs) | 6 | B |
| [PostgresFactDeliveryRecoveryFailureTests](../tests/HostIntegration.Tests/PostgresFactDeliveryRecoveryFailureTests.cs) | 4 | P |
| [PrivateFilesAccessTests](../tests/HostIntegration.Tests/PrivateFilesAccessTests.cs) | 12 | M |
| [SchedulingAccessTests](../tests/HostIntegration.Tests/SchedulingAccessTests.cs) | 1 | M |
| [SchedulingDefinitionTests](../tests/HostIntegration.Tests/SchedulingDefinitionTests.cs) | 11 | M |
| [SchedulingFactCapacityPolicyHistoryTests](../tests/HostIntegration.Tests/SchedulingFactCapacityPolicyHistoryTests.cs) | 2 | B |
| [SchedulingFactCapacityPolicyTests](../tests/HostIntegration.Tests/SchedulingFactCapacityPolicyTests.cs) | 2 | B |
| [SchedulingFactDeliveryRecoveryTests](../tests/HostIntegration.Tests/SchedulingFactDeliveryRecoveryTests.cs) | 4 | B |
| [SchedulingOperationTests](../tests/HostIntegration.Tests/SchedulingOperationTests.cs) | 3 | C |
| [SchedulingRecoveryMaintenanceTests](../tests/HostIntegration.Tests/SchedulingRecoveryMaintenanceTests.cs) | 2 | P |
| [TaskOperationTests](../tests/HostIntegration.Tests/TaskOperationTests.cs) | 12 | C |

## 独占与准备复用

FactRecoveryBrokerJourneyTests、FilesPersistenceJourneyTests、PlatformPersistenceJourneyTests、
IdentityPersistenceJourneyTests、ScheduledCostBusinessJourneyTests、AuditBusinessJourneyTests及
CalendarSchedulingPersistenceTests保持独占。它们普通准备阶段可复制空结构，
进程启动/终止/重启、故障恢复、未迁移拒绝与迁移CLI断言仍按原路径执行。
历史迁移、回滚或升级方法保留自己的新库与实际迁移，不能借模板跳过其验证对象。

OperationJournalCapacityTests与OperationJournalCleanupTests等包含迁移专项的混合类仍独占；
FactRecoveryRollbackConcurrencyTests与FactDeliveryRecoveryEvidenceTests涉及显式历史迁移/回滚，未纳入并行。
其他未列出的类保持独占，后续逐类审核；未列出不代表永远无法并行。

本机仍使用共用测试服务、最多4路，出现压力则正常收尾后降2路；
服务器观察与进程恢复规则见[受控并发](local-test-concurrency.md)。
全量验证以原1097项宿主、1935项整体身份清单为依据，不以覆盖类数量代替耗时收益。

## 默认文件目录修正

初次双轴审查发现ApiTransportTests、MemoryFactCapacityHttpTests、FilesFactCapacityTests、
FactCapacityAdmissionTests、FilesFactCapacityPolicyTests，以及前轮的FactCapacityDiagnosticsTests、
FilesFactDeliveryRecoveryTests曾通过默认file-storage写入同一输出目录。
独立数据库不能证明文件隔离；无上传的宿主也会启动文件恢复扫描。
因此PlatformApp／PersistentIdentityApp在夹具缝上提供所属临时根，宿主正常关闭后清理自身根；
显式路径不被默认值覆盖，也不由另一factory擅自删除。
FilesPostgres_PolicyExpansionAndReceiptSurviveHostRecreation_WithoutChangingStoredBytes显式由旅程
持有一个根，两次宿主使用同一根，验证字节持久性后再清理。PlatformJourneyIsolationTests同时
验证两个真实HTTP宿主的目录不同，关闭一个不会删除另一个的目录或影响它的就绪响应。
修复前这个原有旅程编译通过并在缺少所属根的断言上失败；修正后原有9项扩大回归通过，
最终完整1935项报告也包含该隔离旅程及文件宿主重建旅程，各一次通过。

## 真实子进程的端口归属

首次完整验收在日历提交崩溃旅程启动前失败：Windows SocketException 10013发生于Kestrel绑定，
子进程退出且未开始监听；负载正常返回、所有权idle、没有压力停止信号。该运行不是交付资格。
原PlatformHostProcess和BusinessProcess先用临时TcpListener选端口、释放后再让子进程绑定，
存在端口交接窗口。日志没有证明具体哪个因素触发10013，不能将其认定为数据库并发过载。
两个夹具现让Kestrel直接绑定127.0.0.1:0，ListeningAddress只接受实际生命周期日志中的
HTTP回环根地址并保留原始输出于私有诊断，不将输出转发到普通控制台。
启动等待仍为原20秒，正常失败和进程清理路径保持；不通过重试或放宽预算掩盖启动故障。
依据：[Kestrel动态端口绑定](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0#use-dynamic-port-binding)。

原日历崩溃提交／重启旅程与Costing／Pricing／网关扩大回归共7项通过；
随后修正后的冻结实现重新完整运行，23工程1935项原身份各一次通过，build → tests → format全零。
宿主1097项包含770项并行及327项独占，四路与独占进程均正常返回，所有权idle，私有配置未变。
八护栏和重命名模板也通过；资源与相同边界耗时见[完整测量](host-test-performance.md)。
最终双轴评审、Linux独立四报告Verify、统一门禁及dev资格树以票据118最新原生记录为准。
