# Costing 批次导入、行结论与可恢复检查点

核验日期：2026-10-02。性质：研究与下一条竖切建议，不是实施规格、ADR 或完成声明。按 Matt research 技能调查第一方源码、GitHub 规格与官方资料。本次只新增本文；没有修改 NS/PoS、读取配置或凭据、运行构建/测试、访问数据库或生产接口。

源码基线：NS `81831672aefc1f84db3ee5596a445df88ad26bdb`，PoS `6cb62274491ee50600d4103dc01a8a0a1bea114d`，NSN HEAD `4df714a737ad1e55bcbdf0508b86510e148b6a38`。NSN 同时存在日历调度 #45 的验证中改动，尚未提交；本文没有把它当成已验收能力。[规格 #34](https://github.com/yongpengW/NexusStackNext/issues/34) 仍把长任务、导入/导出列为未完成；[任务扩展 #48](https://github.com/yongpengW/NexusStackNext/issues/48) 及其[准备评论](https://github.com/yongpengW/NexusStackNext/issues/48#issuecomment-5952073786) 明确延迟、列表、条件取消、受约束续租尚未实现，且不包含检查点/批次。

## 1. 结论与证据强度

**建议先完成 #48，再做 Costing 所属的一条可恢复导入竖切。** 提交有界输入、一次获取全部结构错误、查询各行结论、条件取消与从检查点继续应组成一个使用过程。每次业务事务最多改一个 `CostSheet`，批次游标、行结论与计算登记作为执行元数据同时提交。使用既有 Costing → Pricing 事件链，不搬 PoS 的商品、税费、月份或定价公式。依据是 [AGENTS 不变量](../../AGENTS.md)、[ADR-0019](../adr/0019-context-owned-business-tasks.md)、[ADR-0020](../adr/0020-transactional-cost-pricing-cooperation.md) 和 [Costing 词表](../../src/Services/Costing/CONTEXT.md)。

特别修正既有[能力路线研究第 6 节](2026-10-02-ns-pos-capability-parity.md)容易产生的强解释：**PoS 的分段进度文本不是已经证明可恢复的检查点。** 下文区分“源码确有”“建议增加”“尚待验证”；未把读取测试源码当成跑过测试。

| 核验对象 | 源码确有 | 不能由此推出 |
|---|---|---|
| NS Excel 工具 | 表头映射、原工作表行号、每行错误、错误回填 | Costing 已有真实导入调用链；错误文本就是重启游标 |
| PoS 商品导入 | 规范化、完整静态验证、重复行末条生效、详细计数 | 多聚合整批事务适合 NSN；请求重放已有稳定批次身份 |
| PoS 成本重建 | 有界分段、执行许可、代次检查、持久进度文本 | 杀进程后自动跳过已提交段；业务提交与进度同事务 |
| PoS 外部批量通知 | 稳定批次号、内容摘要、行状态、目标代次条件写 | 这些表/专有业务可以搬进共享 Core；排序 JSON 属性就是业务规范化 |
| NSN 成本链 | 单对象输入与任务同事务，结果与 Outbox 同事务，任务 epoch/租约 | 已有父批次、检查点、取消或续租；通用执行器可直接装进新批次表 |

以上各行的具体方法与限制见第 2、3 节。

## 2. NS / PoS 的真实代码模式

### 2.1 保留输入体验，重新决定事务边界

NS 的 [`ExcelReader.ReadAllRows/WriteErrors`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Excel/Import/ExcelReader.cs) 按列定义匹配表头，把原工作表行号放进 `ExcelDataRow.Row`，收集必填和回调错误；`WriteErrors` 把错误写回工作表，并删除成功行。配套 [`ExcelDataRow.GetValue/To`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Excel/Import/ExcelDataRow.cs) 在转换不支持/无效时可能返回默认值或跳过赋值。应保留“定位到用户原始行”的体验，同时在 NSN 把转换失败明确返回为字段错误，不能把无效金额默认为零。本次定向检索 NS 的 Host 和 Core C# 中 `ExcelReader/ReadAllRows/WriteErrors` 没有找到调用点，因而这里仅证明模板工具存在。

PoS 的真实入口是 [`CostManageController.ImportCostProductInfoAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/CostManageController.cs)（1127 行起）：接收 JSON 列表，检查导入操作权限，把整个列表交给服务。它不是后端必须接收 XLSX 的证据。

[`CostProductInfoService.ImportAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/CostManage/CostProductInfoService.cs)（132 行起）的行为值得保留：

- `NormalizeImportRow`（318 行）保留 1 起始输入序号；`NormalizeText`（482 行）把空白变为 null、其余 Trim。
- `ValidateImportRows`（333 行）先检查所有原始行，累计完整错误数，仅展示前 20 个；非法的早期重复行不会因稍后同产品有合法行而悄悄消失。
- 静态验证通过后按产品标识分组、`Last()` 生效；`BuildImportChangesAsync`（238 行）区分输入数、有效产品数、输入重复数、数据库重复数、新增/更新/恢复/未变数。
- 对全部产品排序后获取锁，在一个 `ReadCommitted` 事务里批量插入/修改再提交；这正是 NSN **需要调整**的部分。查询分块和 `SaveChanges` 分批不等于多个提交事务。

NS 的 [`AsyncTaskService.CreateTaskAsync/CreateDelayedTaskAsync/RetryAsync`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/AsyncTasks/AsyncTaskService.cs) 为开发者提供“登记后可执行/重试”的入口；实现是插入任务后发布事件。NSN 应继续提供一次业务调用即可登记的接口深度，不能要求调用方自己依次写业务、记任务、发消息、存游标。这不意味着复制运行时类型反射或不在同一事务内的发布顺序。[ADR-0019](../adr/0019-context-owned-business-tasks.md) 已明确本地任务与 Outbox 的不同职责。

### 2.2 分段、许可与进度不等于可恢复检查点

PoS [`CostBreakEvenPriceRebuildEventHandler.HandleAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/EventHandler/CostManage/CostBreakEvenPriceRebuildEventHandler.cs) 先获取执行许可和高内存任务许可，连接丢失令牌，再通过 `TryClaimTaskExecutionAsync` 取得执行代次。嵌套 `TaskExecutionObserver` 的三个方法有不同含义：

| 方法 | 源码位置与事实 | 可借鉴之处 |
|---|---|---|
| `EnsureCanContinueAsync` | 同文件 388 行起；检查两个许可以及任务当前代次 | 在昂贵阶段前尽早停止，失去执行权需明确失败 |
| `BeginSegment` | 同文件 413 行起；仅写 `currentVersionMonth/currentPlatformId/currentProgress` 字段 | 失败报告能说明当前段；这些字段未持久化 |
| `ReportProgressAsync` | 同文件 427 行起；把月份、段数、处理数拼成文本交给 `TryUpdateTaskProgressAsync` | 操作者能看到进展，但文本不能作为恢复协议 |

[`CostChangeImpactService.ProcessBatchesAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/CostManage/CostChangeImpactService.cs)（436 行起）把目标排序后 `Chunk(MaxRebuildBatchSize)`，每次调用都 `for (index = 0; ...)`。业务生成方法返回后才增加内存计数、报告进度；这里没有读取持久 `NextRow/LastCommittedSegment` 的步骤。[`CostBreakEvenPriceRebuildTaskDto`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Dtos/CostManage/CostBreakEvenPriceRebuildTaskDto.cs) 携带范围和来源执行代次，没有恢复游标。

[`AsyncTaskService.TryUpdateTaskProgressAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/AsyncTasks/AsyncTaskService.cs)（728 行起）按任务 ID、Code、RetryCount、InProgress 条件写 `Result/UpdatedAt`，并在响应不确定时核对最终状态。这证明“进度写回也要检查代次、核对提交结果”，不证明它和成本写入原子提交。仅在业务前调用 `EnsureCanContinueAsync`，检查后仍可能丢许可；NSN 最终提交必须再由数据库裁决。

[`CostMemoryIntensiveTaskExecutionCoordinator.AcquireAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/CostManage/CostMemoryIntensiveTaskExecutionCoordinator.cs) 和 [`CostBreakEvenPriceExecutionLease.EnsureNotLost`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/CostManage/CostBreakEvenPriceExecutionCoordinator.cs) 提供生产资源限制的证据。下一条竖切应有批次大小、分段大小、并发和读取范围限制；不必先引入 Redis 才能保证执行正确性。

### 2.3 批次身份与行结果已有更接近的生产样本

PoS [`ReplenishNotificationBatchService.ReceiveAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/MasterPricing/ReplenishNotificationBatchService.cs)（198 行起）把批次号规范化、限定 1–5000 行，用内容摘要检查已有批次；`ParseItem`（1414 行）保留 `BatchSequence` 和行结论，`MarkDuplicateRows`（1475 行）保留末条并把较早重复项标成 `DuplicateSuperseded`。`RecoverExistingAsync`（1370 行）对同批次号异内容报错，并尝试恢复主任务关联。

接收阶段按 500 行 `SaveChanges/Clear`，但外层只有一个 `BeginTransaction/Commit`；这是**减少跟踪内存**，不是已提交 500 行检查点。先提交批次再创建/关联主任务，靠 `RecoverExistingAsync` 恢复后续动作；NSN 在同数据库可直接把接受快照和可发现工作意图一起提交，减少这类间隙。

同文件 `ClaimNextMasterPricingTargetAsync`（420 行）、`CompleteMasterPricingTargetAsync`（585 行）把父批次执行代次和目标执行代次放进写条件，完成响应不确定时重新查询。可复用的是“目标状态 + 父执行权 + 稳定身份”的协议思想；该源路径不是 NSN 单聚合事务正确性的证明。

[`BatchNotificationContentHash.Compute/WriteCanonicalJson`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/BatchNotificationContentHash.cs) 排序对象属性、保留数组顺序、增量 SHA-256。它对 JSON 结构做规范排序，没有调用商品/金额的业务规范化；不能据此声称 `1`、`1.0`、字符串数字、空白文本都表达同一幂等内容。

## 3. NSN 已有的接口与真实缺口

[`CostingCommands.HandleAsync(UpdateCostInputs)`](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingServices.cs) 先锁 `costing-request/{RequestId}`、读既有任务比对内容，再锁 `costing-item/{ItemId}`、校验 `ExpectedVersion`，在一个事务内改 `CostSheet` 并登记 `CostCalculationEntry`。同身份重放优先于当前版本检查，因此首次成功后版本变了，也仍能返回原工作。

[`CostingExecution.HandleAsync(CompleteCostingWork)`](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingExecution.cs) 在事务外计算演示结果，再调用 [`PostgresTaskExecution.CompleteAsync`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Tasks/PostgresTaskExecution.cs)；后者锁任务并验证 `Running + Epoch + LeaseUntil`，回调只改本地对象和 Outbox，保存后再次检查数据库时间。`ClaimAsync` 用短事务、`FOR UPDATE SKIP LOCKED`，过期接管增加 epoch；`RetryAsync` 保留历史并重开有限预算。

现有 [`CostCalculationEntry`](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingDbContext.cs) 有一个必需 `ItemId` 外键；共享执行器的 SQL 固定操作所属 schema 的 `tasks` 表；[`DurableTaskRecord`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Tasks/DurableTaskRecord.cs) 没有批次游标。**不能靠继承它、制造假 ItemId 或把任务表改成任意 JSON 就宣称批次接入。** #48 解决的公共执行权策略可继续复用；批次输入、游标、行结果、查询和编排先放 Costing。出现第二个真实批次消费者后，再判断是否值得提取实现。

已有测试源码包含 [`Completion_CommitsCostAndItsVersionedEventTogether/OutboxFailure_RollsBackCostAndTask_ThenSameLeaseCanRecover`](../../tests/Costing.IntegrationTests/CostingWorkflowTests.cs)、[`GatewayCostUpdate_CrossesTwoDatabases_AndDuplicateDeliveryAfterRestartHasNoEffect`](../../tests/Costing.IntegrationTests/BusinessCooperationTests.cs) 和 [`TaskRegistrationFailure_RollsBackInboxAndCost_SameMessageCanBeRedelivered`](../../tests/Pricing.IntegrationTests/CostIngestionTests.cs)。这些是可延伸的公开测试面，不是新增批次能力的验收结果。

## 4. 建议的第一条完整竖切

### 4.1 选择一个有界业务输入，提供一次提交与一次查询的体验

建议输入固定为 `BatchRequestId + SchemaVersion + Rows[]`；每行是 `ItemId + ExpectedVersion + PurchaseCost + FreightCost`，仅使用现有演示 CostSheet 规则。默认重复策略固定为“完整验证所有原始行后，同 ItemId 的末条生效”，保留每个来源序号，早期重复行有明确结论，避免悄悄少算。不要移植 PoS 的产品恢复、脏库重复记录修复等业务规则。[CostSheet.IsValidInput/UpdateCost](../../src/Services/Costing/NexusStackNext.Costing.Domain/CostSheet.cs) 已规定非负、四位小数、合计上限和同值不增版本。

建议初始上限（待容量验证，不是已测阈值）：1–5000 行；JSON/CSV 输入最多 2 MiB；每段读取 100 行、配置最大 500；返回前 20 个结构错误并给完整错误数；行结果分页默认 50、上限 200；单工作循环一次只执行一个批次。解析深度、字段长度和并发接受数也应有界。HTTP 请求上限在读取/反序列化前生效，不能只依赖 `Content-Length`，分块请求也要按实际字节计数。ASP.NET Core 官方区分缓冲与流式上传，并提示并发缓冲的内存/磁盘压力；这些上限应在真实入口验证。[官方上传文档](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0)

行号区分 `Sequence`（规范化记录序号，恢复依据）与 `SourceRow`（文件中用户看到的行号，显示依据）；CSV 含引号换行时两者尤其不同。批次一经接受，保存规范化输入快照与来源位置，执行中不重读用户可修改的文件、不重新枚举商品集合、不按活表 offset 分页。

调用方提交一次批次即可完成登记，后台自行继续；查询返回结构化计数、当前执行代次、可领取/租约时刻、已提交行位置、稳定错误码和关联计算任务。版本列应由现有查询/模板准备体验提供，不要求使用者手工逐行调用内部领取接口。可以提供预检，但预检版本不是提交时的并发保证；执行时不得偷偷改为“读取最新版本后强行覆盖”。

### 4.2 规范化幂等是一项业务契约

先定义并版本化规范化规则，再计算摘要；不要 hash 原文件字节或任意 DTO 默认序列化结果。

- GUID 转固定格式，版本按精确 Int64 解析；金额先用 decimal 验证，再以固定不变文化的四位小数字符串编码，等值 `1` 与 `1.0000` 同义。超精度值报错，不能先舍入再接受。
- 固定字段名及顺序；包含 SchemaVersion、固定重复策略、原始逻辑行序列和全部原始行的规范化业务内容，不能只 hash 去重后的赢家。行顺序有末条生效含义，调整顺序属于不同请求。
- JSON 属性顺序、无意义外部空白、文件名、CSV/XLSX 容器格式不参与业务摘要；文件来源位置保存首次接受值。未知业务字段和重复 JSON 属性明确拒绝，避免两种解析器解释不同。
- 保存首个接受摘要及规范化快照。相同 BatchRequestId/内容返回原批次和首次接受时间；不同内容返回 409；取消/失败后重放同身份仍返回原结论，不静默创建新批次。
- 数据库唯一约束与串行化同请求的短事务决定并发首接收者；每个有效行的 `RowTaskId` 在首次接受时由调用方/注入生成器提供并持久化，重试、接管、改分段大小均不换身份。后续任务以 `Origin=batch` 区分手工/计划请求，不能被同 Guid 的手工请求当作自己的重试。

这是对 [`CostCalculationEntry.Matches`](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingDbContext.cs) 和 PoS 批次摘要的业务化延伸；HTTP 仍遵守 [Int64 字符串契约](../http-int64-contract.md)。内容摘要负责识别内容，不替代授权和调用者身份。

### 4.3 静态错误与运行期冲突分开，部分成功必须可见

| 时间 | 例子 | 建议结论 |
|---|---|---|
| 接受前，整个输入结构无法可靠解释 | 语法/表头错误、空批次、超限、字段类型无效、金额精度/范围不合法 | 整批 400/413，无业务修改、无可执行批次；返回安全字段错误及原始行号；修正后重新提交 |
| 接受后，业务状态已变化 | ExpectedVersion 不匹配、创建目标已存在、领域明确拒绝 | 行终态 `Rejected` 与稳定码；该行不改业务，其余行继续 |
| 重复输入被固定策略替代 | 同 ItemId 的较早行 | `DuplicateSuperseded`，关联生效行；不注册计算任务 |
| 技术暂时失败 | 连接失败、死锁、提交响应不确定 | 不伪装业务拒绝、不推进该行；核对已提交行结论，再有限重试 |
| 任务执行权丢失 | 取消、租约过期、代次变化 | 旧执行者停止；不能用新的失败状态覆盖已有结论 |

EF Core 把乐观并发更新失败报为 `DbUpdateConcurrencyException`，新增主键冲突通常是 provider-specific 异常；不能捕获所有 `DbUpdateException` 就声称“行版本冲突”。要识别具体约束/异常并决定重试还是拒绝。[官方并发文档](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)

静态验证全通过不代表所有业务行将成功。批次接收返回 202，轮询用 200 返回当前状态与计数；不要用普通 200 文案“全部导入成功”掩盖 `Rejected`。建议对外显示 `Imported`、`Unchanged`、`DuplicateSuperseded`、`Rejected`、`Pending`，并分开列出计算任务的 Pending/Running/Succeeded/Superseded/Failed。部分行被业务拒绝时导入终态为 `CompletedWithErrors`；所有有效行均被拒绝也不叫成功。统计应能解释每条原始行，重放不增加计数。

## 5. 核心边界：父执行权、单行事务和游标一起裁决

### 5.1 批次是执行编排，不是跨对象的大聚合

建议在 Costing 内增加不可变输入与行执行元数据（暂称 `CostImport/CostImportRow`，仅建议名），沿用 [ADR-0019](../adr/0019-context-owned-business-tasks.md) 的执行元数据定位。初次接收事务只保存有界快照、身份/摘要与可发现的待执行意图，不改任何 CostSheet。每次行提交只修改一个 CostSheet 及该工作的元数据；不把整批对象包装成一个业务聚合来规避不变量。若将来业务真要求“跨商品全部生效或全部不生效”，应重新建模业务过程，不能在此入口加大事务开关。

最小可恢复切面是：

```text
接受快照（0 个业务聚合）
  → 领取父批次（短事务，epoch + 有限租约）
  → 读取一段不可变行（事务外）
  → 行 1：检查执行权 + 1 个 CostSheet + 计算任务 + 行结论 + 游标（事务）
  → 行 2：同样一个独立事务
  → 释放本段 scope / 跟踪对象；读下一段
  → 导入终态（元数据）；逐行计算继续既有 Costing → Outbox → Pricing 链
```

“分段 100 行”指读取、内存和调度批量，**不是 100 个 CostSheet 一次提交**。进度展示可以节流，但恢复依据每行持久化。`NextSequence` 只越过连续的已提交终态；首版顺序执行，不用“已处理条数”冒充存在洞时的游标。唯一 `(BatchId, Sequence)` 行结论/稳定 RowTaskId 是重放证据；汇总计数和游标与该行一同提交。

### 5.2 锁顺序必须覆盖取消和旧执行者

建议 `ApplyImportRow(BatchId, Epoch, Sequence)` 在 Costing 自己的命令适配器内拥有完整事务，锁顺序固定为：

1. 锁父批次执行行 `FOR UPDATE`；取数据库当前时间，检查 Running、Epoch、一开始有效的租约、未取消和预期游标。
2. 查该行既有结论。已有终态只返回原结论；新的行锁/稳定任务身份锁排在父锁之后。
3. 沿现有顺序获取 `costing-request/{RowTaskId}`，再获取 `costing-item/{ItemId}`；在同一个数据库事务内检查目标 `ExpectedVersion`。
4. 成功：创建/修改一个 CostSheet，登记包含当前 `InputRevision` 的稳定计算任务，写行结论和关联任务 ID，推进游标/计数。确定性业务冲突：仅写行拒绝与进度。均不可从这里调用外部服务或 broker。
5. 保存后，在仍持父锁的事务内再次用数据库时间确认租约并提交；失败全部回滚，不把内存游标当成已提交游标。

领取、续租、取消、标记批次失败/终态都先锁同一父执行行。人工单对象更新保持 request → item，不能再反向索取父批次锁。整条锁图保持父批次 → 行/请求或子任务 → item；任何新增路径若从 item 反向锁父都需要调整。PostgreSQL 官方说明行锁持有至事务结束、`FOR UPDATE` 会等待并返回更新后行，并建议多对象统一加锁次序；事务级 advisory lock 也在事务结束时释放。[官方锁文档](https://www.postgresql.org/docs/current/explicit-locking.html)

**不能这样实现：** 外层检查父批次还在运行 → 循环 `sender.SendAsync(UpdateCostInputs)`（每次自己提交）→ 最后另存行结论/游标。父取消可能恰好在检查后提交；也可能输入已成功、进程却在游标前崩溃。这条路径没有所需的原子边界。应在 Costing 内复用不自行提交的局部业务实现，让所属的批次行命令决定一次提交；无需把接口提到 BuildingBlocks。[ADR-0017](../adr/0017-context-owned-command-transactions.md) 已明确“仓储登记、所属命令决定提交”的原因。

数据库时间使用 `clock_timestamp()`，不能改为事务起始时间 `CURRENT_TIMESTAMP` 来做等待锁后的到期判断；两者官方定义不同。[时间函数文档](https://www.postgresql.org/docs/current/functions-datetime.html#FUNCTIONS-DATETIME-CURRENT)

租约判断的线性化点要写进规格：数据库在持有排他父锁时判断当前执行权，随后提交；取消/接管不能穿过这段事务。不能用客户端收到响应的时刻判断“迟到”，也不应声称一次提交前检查能保证磁盘提交或 HTTP 响应绝对早于某个墙钟时刻。单行短事务应有超时，避免长等锁阻塞续租；耗时解析/计算/IO 在事务外。

### 5.3 首版建议的取消含义，以及更强语义需要的代价

建议父批次取消表示“从当前已提交检查点停止接受后续行”。已提交 CostSheet 输入、行结论和已经登记的计算任务保留，计算与 Pricing 投递继续；对外说清 `ImportState` 与 `CalculationState`。这符合批量导入的边界，也接近 [#48](https://github.com/yongpengW/NexusStackNext/issues/48) 对已提交输入/结果不可回滚的要求。需要停止某个尚可取消的子计算时，使用 #48 的所属任务条件取消。

- 取消先获父锁并提交：旧行命令读到取消，输入、子任务、进度均不提交。
- 行提交先赢：取消在它之后裁决，返回的已提交行数包含该行；以后的行停止。
- 最后一行把导入终态一起提交：后来的取消明确冲突，不能在已完成批次上伪装成功。
- 部分取消保留已完成行；未处理行显示 `NotProcessed`/父取消派生状态。重发同批次身份返回原取消结论，恢复新业务意图需新身份。

**若产品要求“父批次取消后，任何尚未提交的子计算也不能产生成本结果/Outbox”**，上述首版语义就不够。届时子 `Complete/Fail/Renew` 等写路径须在自己的事务里先锁父批次、再锁子任务、最后锁 item，复核父取消策略与子执行权；取消与结果同一屏障竞争。不能在已有 `CompleteAsync` 锁子任务之后再去锁父，也不能只由父循环向子传取消令牌。已提交输入仍保留，已提交结果和事件仍不能撤回。这是另一项明确验收义务，不能免费附会到首版取消上。

## 6. 崩溃、接管与不确定提交

| 断点/竞争 | 恢复必须看到的事实 | 错误实现的特征 |
|---|---|---|
| 接受事务提交前终止 | 没有可运行的半批次；相同身份可重试 | 元数据存在但快照不全/任务不可发现 |
| 接受已提交、响应丢失 | 同身份返回同批次、原摘要、原行任务 ID | 再建一个批次或重新生成全部任务 ID |
| 一行开始前/事务内终止 | 该行全部回滚；接管仍处理该行 | 只存了游标，永久跳过未登记业务 |
| 行事务已提交、进程尚未更新内存即终止 | 业务输入、子任务、行结论、游标同时存在；重启从下一行继续 | 文本进度落后导致重复改业务 |
| 单段中途终止 | 从段内最后已提交行继续，不重做整个段 | 仅按 SegmentNumber 恢复 |
| 提交确认断线 | 新 scope 查询 `(BatchId, Sequence)` 结论；未知时继续有界等待/重试 | 一律假定回滚、改用新身份 |
| 租约过期后新 epoch 接管 | 旧 epoch 的行写、进度、失败、终态、续租全部被拒绝 | 只挡最终 Complete，仍允许旧进度或下一行写入 |
| 取消与行/末行提交竞争 | 按父锁顺序得到一种合法结论 | 取消成功后仍新增下一行输入/任务 |
| 行版本冲突 | 拒绝该行，保留其他已提交行，游标可继续 | 把 ExpectedVersion 改最新后无声覆盖 |
| 子计算被较新输入取代 | 子任务为 Superseded，不发旧结果；导入历史仍真实 | 为了批次全绿反复把旧输入写回来 |

EF 官方明确提交期间连接中断可能导致事务结果未知；因此稳定身份和重新读取事实是协议的一部分，而不是只给请求加一次重试。[连接恢复与提交不确定性](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue)

续租依赖 #48 的既定约束：同 epoch、仍 Running、未过期，数据库时间，租约单调延长且总期限锚定本次领取开始时刻。检查点推进不能自动刷新租约、清空尝试历史或绕过最长执行期限。到达上限仍未完成时停止当前执行，让后续有效执行从持久检查点继续；具体等待/失败与预算沿 #48 明确规则，不能靠无穷心跳解决。取消是业务命令，宿主 `CancellationToken` 是协作停止信号，两者不是同一个事实来源。

## 7. CSV / JSON / XLSX：保持易用性，不重造 Excel

| 格式 | 对本竖切的价值 | 必须明确的成本 | 建议 |
|---|---|---|---|
| JSON | PoS 导入真实 HTTP 已用 JSON 列表；便于程序/已有前端一次提交规范化行 | 精确数字、未知字段、重复属性、体积/深度限制 | 作为稳定应用命令对应的 API；不能因测试容易就要求终端用户手工写 JSON |
| CSV | 可提供下载模板，表格软件编辑后导入，适合少量固定列 | 引号、分隔符、换行、编码、小数文化及 Excel 转换长标识 | 首个文件适配器采用有明确方言的 UTF-8 CSV；支持可选 BOM，模板标识列按文本说明；不使用 `Split(',')` |
| XLSX | 保留表格用户熟悉的上传、列标题与行错误体验 | ZIP 解压/实际单元格上限、公式/缓存值、日期与数字类型、空工作表、依赖许可 | 有实际工作簿入口需求就用成熟库，仍输出同一规范化行；不能声称做了 JSON 就已完整兑现 Excel 导入 |

CSV 引号中的逗号、换行和双引号有明确格式规则。[RFC 4180](https://www.rfc-editor.org/rfc/rfc4180.txt) 是一手格式来源，但它不是用户 Excel 区域设置的全部行为；具体模板兼容性仍需样本验证。金额列仅接受明确数字文本/数值，不执行公式；错误文件中的用户文本按文本单元格处理，避免把内容解释为公式。SourceRow 显示原位置，不随清洗/跳过空行重新编号。

NS 的 [`NexusStack.Excel.csproj`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Excel/NexusStack.Excel.csproj) 引用 EPPlus 8.7.1；官方当前许可页面说明商业环境需商业许可，不能作为 NSN 默认模板依赖直接搬入。[EPPlus 官方许可](https://epplussoftware.com/en/LicenseOverview)；仓库约束见 [ADR-0008](../adr/0008-no-commercial-dependencies.md)。[ClosedXML 官方仓库](https://github.com/ClosedXML/ClosedXML) 标明 MIT，提供 XLSX 高层读写，可作为有界文件候选；选定时仍应核验版本和依赖。若将来真实大文件要求流式读取，Microsoft 提供 Open XML SDK `OpenXmlReader` 的 SAX 方案，指出 DOM 会整块加载内存；这不是现在重写工作簿格式的理由。[官方大表读取](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-parse-and-read-a-large-spreadsheet)

建议交付 JSON + 固定列 CSV 适配器、可下载模板、可分页行错误；XLSX 若进入同票则只增加薄解析适配器和文件样本验证，不复制持久任务协议。解析器接口上会变化的是**输入格式及来源位置提取**，Costing 规范化/并发/任务语义不会跟着 Excel 库变化。私有错误成果或导出文件通过 Files.Contracts 访问；Files 不读取 Costing 表，Costing 不读取 Files 表。私有异步导出仍是 #34 的另一条验收义务。

## 8. 下一票应固定的决定与验收

以下是建议的拆票输入，本研究没有创建或修改票据：

1. **前置状态**：#48 的条件取消、续租、列表和迁移先验收；#45 日历功能继续按其独立票据完成，不能作为批次已经可恢复的依据。
2. **精确产品边界**：确认父取消默认停止导入登记、已登记计算继续；确认“导入处理完成”与“全部成本/定价算完”分开。若选择更强级联取消，加入第 5.3 节额外锁屏障和验收，不在实现中暗改语义。
3. **输入合同**：固定上限、格式、规范化版本、末条生效、行号、静态全拒绝/运行期部分成功；同身份异内容 409。单行终态保留原任务与版本，技术失败有限重试，修正业务冲突用新身份。
4. **所属与迁移**：只在 Costing 保存批次快照、行执行元数据和恢复依据；普通启动不迁移；旧单项任务不用编造 BatchId/CreatedAt/输入来源。查询不得加载整个大批次及全部尝试历史；清理后幂等身份保留期必须显式规定，首版不自动删除。
5. **公开测试面**：HTTP/ISender 验证，使用真实 PostgreSQL 的确定性屏障；至少覆盖接收并发重试、数值等价、换序冲突、静态错误、末条生效、部分拒绝、段内杀进程、提交响应不确定、过期接管和旧 epoch 的全部写路径。应在第二段以前已经有真实已提交行，不能只测空批次恢复。
6. **原子性反向验证**：分别让子任务登记、行结论写入、游标写入失败，断言该行 CostSheet 一并回滚；先前已提交行保留。取消分别先于行提交、晚于行提交、与最后一行竞争，证明所有合法结论；读源码和绿色构建不能替代这些测试。
7. **跨上下文旅程**：真实网关提交含成功/冲突/重复的输入 → 查询批次和行结果 → 中途取消或杀进程 → 重启查同检查点 → 子计算完成 → Pricing 查询本地成本版本/演示结果。broker 失败时导入/成本事实仍可查，Outbox 恢复后 Pricing 去重接受。`Delivered` 仍仅表示 broker 确认，见 [Costing/Pricing 操作说明](../costing-pricing-cooperation.md) 与 [Pricing 词表](../../src/Services/Pricing/CONTEXT.md)。
8. **体验与门禁**：文件模板和来源行错误要可用，普通用户越权与超限请求经网关验证；不得为缩短测试把真正的批次使用流程砍成内部方法样板。正式实施完成后执行 build → 串行 tests → format、必要仓库检查、本机与 Linux CI、双轴评审；本研究未执行这些门禁。

当前仍待验证：合适的行数/字节/分段阈值；真实 CSV/XLSX 样本；批次元数据与 #48 共享执行实现的最小接点；取消定义是否需要覆盖已登记子计算；故障注入下的锁顺序、事务提交和版本行为。第二个真实批次消费者出现之前不提取通用 batch 框架；单机进程恢复与并发正确性先做，多机 HA、容量 SLO 和灾备仍按 #34 放在最后。
