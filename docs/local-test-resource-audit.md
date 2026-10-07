# 本机并行类的资源审查

本轮相对dev fcee91c扩大显式清单。以下是具体类与资源归属的审查记录，
分类用于说明代码路径，运行时只读取明确类名；不按正则或名称自动批准新增类。
候选87类770项，327项仍独占；最终资格与交付见[扩大宿主并行与初始化复用](https://github.com/yongpengW/NexusStackNext/issues/118)。

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
