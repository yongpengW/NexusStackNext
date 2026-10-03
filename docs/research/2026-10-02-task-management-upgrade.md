# 业务任务管理升级：首次接受时间、取消与受约束续租

核验日期：2026-10-02；源码基线 `77bd56bcf986f2b2e9dfbfcd5e6c43d6aecde850`。本文服务于[业务任务管理：延迟、列表、取消与受约束续租](https://github.com/yongpengW/NexusStackNext/issues/48)及其实施前评论。**这是源码与官方资料研究，以下实现方案和测试均为建议，未实测。** 本次只新增本文，没有运行构建/测试、访问数据库、读取环境文件或凭据。

## 1. 已有协议与升级范围

[`PostgresTaskExecution`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Tasks/PostgresTaskExecution.cs) 已在所属 DbContext 的短事务内领取、完成、失败和条件重试；领取用 `FOR UPDATE SKIP LOCKED`，完成/失败用任务行锁、epoch 与数据库时间，并在 `SaveChangesAsync` 后再次检查租约。当前没有取消/续租；[`DurableTaskRecord`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Tasks/DurableTaskRecord.cs) 没有不可变创建时间或本次领取的总期限，`AvailableAt` 会被失败/人工重试改写。不能据此还原历史创建时间。

两个真实消费者、映射及公开请求分别在 [Costing DbContext](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingDbContext.cs)、[Pricing DbContext](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingDbContext.cs)、[共享映射](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Tasks/DurableTaskMapping.cs)、[Costing 请求](../../src/Services/Costing/NexusStackNext.Costing.Application/CostingRequests.cs)、[Pricing 请求](../../src/Services/Pricing/NexusStackNext.Pricing.Application/PricingRequests.cs)。共享实现继续只操作调用方数据库；业务结果、公式、输入和事件留在上下文内，不增加中央任务库、批次或导出框架。[ADR-0019](../adr/0019-context-owned-business-tasks.md)、[ADR-0020](../adr/0020-transactional-cost-pricing-cooperation.md)

## 2. 首次接受时间：迁移不伪造历史

**数据库事实：** PostgreSQL 新增无默认值的列时旧行值为 null；`ADD COLUMN ... DEFAULT clock_timestamp()` 会对旧行计算迁移时刻的值，且需要更新旧行；之后单独 `ALTER COLUMN ... SET DEFAULT` 只影响未来插入。[PostgreSQL 修改表](https://www.postgresql.org/docs/18/ddl-alter.html#DDL-ALTER-ADDING-A-COLUMN)

建议两个上下文各生成独立迁移，再人工检查并改为以下顺序；模型和 snapshot 仍声明 nullable `CreatedAt` 与 `HasDefaultValueSql("clock_timestamp()")`。旧行不做回填；新增 nullable `MaxLeaseUntil` 同样不回填、不设默认。示意 SQL 的 schema 由所属迁移固定：

```sql
ALTER TABLE costing.tasks ADD COLUMN "CreatedAt" timestamptz NULL;
ALTER TABLE costing.tasks ALTER COLUMN "CreatedAt" SET DEFAULT clock_timestamp();
ALTER TABLE costing.tasks ADD COLUMN "MaxLeaseUntil" timestamptz NULL;
```

EF 官方要求审查生成迁移，允许用 `migrationBuilder.Sql` 自定义；不能只看最终模型就认为升级语义正确。[EF 迁移管理](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing#customize-migration-code) 新列带默认会填旧行也是 EF 文档明确提醒的行为。[EF 生成值](https://learn.microsoft.com/en-us/ef/core/modeling/generated-properties#default-values)

**新请求建议：** 在请求身份锁、对象锁及首次接受校验完成后，取一次数据库 `clock_timestamp()`，将同一个值用于 `CreatedAt` 和 `AvailableAt = CreatedAt + delaySeconds`；这满足时间由数据库裁决，并避免两个 volatile 默认值采样不同。显式设置非默认属性值会覆盖数据库默认，所以只允许适配器使用刚取到的数据库时间，不接受客户端创建时间。[EF 覆盖生成值](https://learn.microsoft.com/en-us/ef/core/modeling/generated-properties#overriding-value-generation)

若选择让插入默认生成 `CreatedAt`，应在 `SaveChanges` 后使用返回值；不能在保存前把 null 映射成响应，也不能在响应时重新取时间。Npgsql 10.0.3 的 [`AppendInsertOperation/AppendInsertCommand`](https://github.com/npgsql/efcore.pg/blob/v10.0.3/src/EFCore.PG/Update/Internal/NpgsqlUpdateSqlGenerator.cs) 将生成列作为读取列放进 `RETURNING`，PostgreSQL `RETURNING` 可返回默认生成值。[PostgreSQL RETURNING](https://www.postgresql.org/docs/18/dml-returning.html) 显式赋值的普通期限列则不能假定 EF 自动回读舍入后的值。

三个人工入口是 [`UpdateCostInputs`](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingServices.cs)、[`UpdatePricingCost`](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingServices.cs)、[`UpdatePricingFee`](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingFeeCommands.cs)。建议统一以下约束，公开命令验证和 HTTP 都覆盖：

- `delaySeconds` 是整数 `0..2_592_000`，省略等价于 0；以规范化整数参与已有 `RequestId + Origin + 输入 + ExpectedVersion` 的内容比较。不同延迟复用身份冲突；先查既有请求，再检查当前聚合版本，重放返回原时间、原任务及当前结论。
- 持久化请求的原始 `DelaySeconds`，不要由会变的 `AvailableAt` 反推。旧任务补 0 有依据：旧入口没有延迟参数；旧 `CreatedAt` 仍为 null。相同旧请求省略/显式 0 仍可重放，不同延迟冲突。
- 取消和人工重试都不修改 `CreatedAt` 或原请求延迟；人工重试只按现有规则重新设置可领取时刻和预算。
- [`ScheduledCostIngestion`](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/ScheduledCostIngestion.cs) 与 [`PricingCostIngestion`](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingCostIngestion.cs) 当前登记即到期任务。事件继续使用 0 延迟，不把事件时间、计划发生时间或人工延迟混为本地任务首次接受时间。

以上是[票据契约](https://github.com/yongpengW/NexusStackNext/issues/48)与既有身份检查的延伸；金额规范化沿用各上下文规则，不在本票引入新的通用请求摘要协议。

## 3. 续租：固定本次领取的上界，复检原租约

建议扩展 [`DurableTaskOptions`](../../src/BuildingBlocks/BuildingBlocks.Application/Tasks/DurableTaskOptions.cs) 为明确有界的总领取预算（名称例如 `MaxLeaseDuration`），验证 `LeaseDuration <= MaxLeaseDuration <= 明确的有限上限`。每次成功 Claim 在数据库时刻 `t0` 持久化 `MaxLeaseUntil = t0 + MaxLeaseDuration`；该上界随本次 epoch 固定，续租或宿主重启不重算。只有新的成功领取创建新上界。升级前 Running 且上界 null 的工作不允许续租，可在原租约内完成/失败，或到期接管后由新 epoch 获得预算。[票据实施前约束](https://github.com/yongpengW/NexusStackNext/issues/48)

建议 Renew 只走所属 `ISender`，按以下短事务执行；返回至少包含 `TaskId/Epoch/ExpiresAt` 的实际结论：

1. `FOR UPDATE` 锁任务行，锁拿到后读取数据库时间 `t1`；要求 Running、epoch 相同、`oldLeaseUntil > t1`，且持久 `MaxLeaseUntil` 非 null。
2. 保存原值 `oldLeaseUntil`；候选新值取 `min(t1 + LeaseDuration, MaxLeaseUntil)`。按数据库精度处理后要求 `newLeaseUntil > oldLeaseUntil`，否则返回明确冲突/预算已尽；不改 epoch、尝试次数、领取时间或历史条数。
3. 保存新期限；完成数据库写入后、提交前再次读取 `clock_timestamp()`，要求 **原** `oldLeaseUntil` 仍严格大于该时刻。不满足就回滚，不能拿已延长的新值证明原执行权仍有效。
4. 提交成功才返回续租成功与实际存储期限；复检失败不留下新期限。新的 scope 查询要得到同一微秒值，调用者以返回的期限和后续条件写入结果判断执行权。

这是对现有 Complete/Fail 复检模式的建议性扩展，保证的是**持有任务行锁期间的有效条件裁决，以及成功提交后对其他操作可见的顺序**。`FOR UPDATE` 阻止并发修改直至事务结束；等待者读取更新后的行。[PostgreSQL 行锁](https://www.postgresql.org/docs/18/explicit-locking.html#LOCKING-ROWS) 最后一次时钟检查与真正 COMMIT 仍有间隔，网络、进程调度或数据库提交都可延迟；不要宣称“物理提交瞬间一定早于截止时间”。尽量把检查放在最后、缩短事务，条件更新可减少往返；本票不引入数据库提交时刻调度保证。

**精度也是执行权边界：** `clock_timestamp()` 随调用变化，`now()/CURRENT_TIMESTAMP` 固定在事务开始，不能用后者识别锁等待期间过期。[PostgreSQL 当前时间](https://www.postgresql.org/docs/18/functions-datetime.html#FUNCTIONS-DATETIME-CURRENT) `timestamptz` 为微秒，.NET 为 100ns；Npgsql `DateTimeOffset` 只支持 offset 0。[Npgsql 时间映射](https://www.npgsql.org/doc/types/datetime.html) 当前包版本是 [10.0.3](../../Directory.Packages.props)，其 [`PgTimestamp.Encode`](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/Converters/Temporal/PgTimestamp.cs) 用 ticks 差值除以 10，截掉额外精度。

因此建议策略时间限定为整微秒（或更粗的整毫秒），在写入前按同一精度计算、比较，并通过回读/`RETURNING` 确认返回期限；不能把仅多 1 tick、数据库仍存同一值的心跳称为成功。有效条件统一为 `LeaseUntil > now`，相等即过期；总期限同样不得因舍入向未来越界。数据库时钟是裁决源，不把它误称为单调时钟。

## 4. 锁顺序、取消竞争和未知提交

现有接收路径为“请求身份 advisory lock → 读取既有任务 → 对象 advisory lock → 写对象及新任务”；[`Costing Complete`](../../src/Services/Costing/NexusStackNext.Costing.Infrastructure/CostingExecution.cs) 与 [`Pricing Complete`](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingExecution.cs) 为“任务行锁 → 对象 advisory lock → 业务结果/Outbox 或缓存失效记录 → 尝试记录”。建议 Cancel/Renew/Fail/Retry 只沿任务行锁进入，需要改尝试时在其后；不要新增“持有对象锁，再等既有任务行锁”的倒序路径。统一顺序可以避免相互等待环；数据库仍可能报告死锁，不能把异常当成业务成功。[PostgreSQL 死锁](https://www.postgresql.org/docs/18/explicit-locking.html#LOCKING-DEADLOCKS)

建议 Cancel 校验观察到的 epoch，仅接受 Pending/Retry/Running；保留 epoch 作为重复取消身份，状态改为 Cancelled。Running 同事务把本次 attempt 结束为 Cancelled；Pending/Retry 不伪造新的执行尝试。重复同 epoch Cancelled 返回原结论，其余终态或不同 epoch 冲突。Cancelled 永不被 Claim/Retry 重新开放；再次发起用新 RequestId。这些是[票据明确要求](https://github.com/yongpengW/NexusStackNext/issues/48)。

| 同一任务上的竞争 | 应允许的线性顺序与可观察结论（建议测试） |
|---|---|
| Cancel ↔ Claim | 取消先提交：任务不可领取；领取先提交：epoch 已增加，携带旧 epoch 的取消冲突。 |
| Cancel ↔ Complete | 取消先提交：完成为 false，无新结果/Outbox；完成先提交：取消冲突，已有结果和事件保留。 |
| Cancel ↔ Fail | 取消先提交：失败回写被隔离；Fail 先进入 Retry 后同 epoch 取消可成功，二者成功是合法串行结果；若 Fail 已进入 Failed，取消冲突。 |
| Cancel ↔ Retry | Cancelled 不能重试；Failed 不能直接取消；Retry 先把 Failed 改为 Retry 后同 epoch 取消可成功。不要断言任意竞争必有且仅有一个成功。 |
| Cancel ↔ Renew | 取消先提交则续租冲突；续租先提交后取消仍可成功，取消后的旧执行者不能写。 |
| Renew ↔ 到期接管 | 原租约仍有效才可续；新 Claim 已接管则旧 epoch 续租/完成/失败均失败。旧 Running 无总上界只能完成、失败或被接管。 |

取消只改变执行元数据，不回滚已接受的聚合输入，不删除既有历史或已提交结果，不撤回已发布消息。所有业务修改与执行结论仍在同一所属数据库事务内。[ADR-0019](../adr/0019-context-owned-business-tasks.md)、[ADR-0020](../adr/0020-transactional-cost-pricing-cooperation.md)

连接在 COMMIT 时断开会产生未知提交，EF 不能证明已回滚。[EF 提交失败与幂等](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue) 建议丢弃该 DbContext，用新 scope 经公开查询核对：首次接受按 RequestId；取消按 TaskId + epoch + Cancelled；续租按当前 epoch/状态/实际期限；完成核对任务、业务结果及 CostDelivery。查询只能确认当前事实，无法凭没有操作回执的期限反推“某一次心跳恰好执行一次”；不把未知异常翻译成取消成功/续租失败且保证未提交。Claim 响应丢失时让租约到期恢复，不猜测取得了哪个任务。

## 5. 有界元数据列表：稳定排序不等于跨页快照

现有 [`ApiPage/ApiPageRequest`](../../src/BuildingBlocks/BuildingBlocks.Web/ApiPage.cs) 是 `page/limit`，默认 50、最大 200，`Offset` 用 long。建议保留形状，两个上下文分别提供应用查询及授权 HTTP 适配器；SQL 直接投影 TaskId、ItemId、State、CreatedAt?、AvailableAt、Epoch、Attempts、LeaseUntil?、MaxLeaseUntil?、ErrorCode，不加载输入或 `History`。详情保留既有历史，补相同时间元数据。[票据列表契约](https://github.com/yongpengW/NexusStackNext/issues/48)

建议首版明确 `CreatedAt DESC NULLS LAST, TaskId ASC`（LINQ 可先按 `CreatedAt == null` 升序，再按日期降序与 ID 升序），不按会随重试变动的 AvailableAt 排序；校验状态白名单、非空业务对象 ID 和分页参数。旧 null 创建时间作为未知组，以 TaskId 决定组内顺序；索引与实际筛选/排序匹配后再测计划，不假定现有 `(State, AvailableAt)` 就够。

建议为这个列表单独声明可浏览偏移上限，例如 `Offset <= 100_000`，超过返回稳定 400 范围错误并提示收窄筛选；阈值是待实施确认的性能取舍，不是官方限制。始终先以 long 计算并验证再转 int，禁止溢出、静默夹到 `int.MaxValue` 或加载全部再分页。即使单页有限，深 OFFSET 仍需处理被跳过的行。[EF 分页](https://learn.microsoft.com/en-us/ef/core/querying/pagination#offset-pagination)

完整唯一排序只消除相同排序键的歧义；并发插入或状态变化仍会使页间重复/遗漏。[EF 排序警告](https://learn.microsoft.com/en-us/ef/core/querying/pagination) 若返回 total 与条目应彼此一致，建议在同一个短的 Repeatable Read 只读事务内做 count + page；Read Committed 下两条查询可能看到不同快照。[PostgreSQL 隔离级别](https://www.postgresql.org/docs/18/transaction-iso.html) 每次 HTTP 请求仍是新快照，不能宣传整个翻页过程一致；不为本票引入无限游标或跨请求长事务。列表也不用 `SKIP LOCKED`，否则会遗漏正在操作的任务。

## 6. 最小验证面：逐条竖切，不以读表代替结果

以下均为待实现测试建议，沿用已有 [CostingWorkflowTests](../../tests/Costing.IntegrationTests/CostingWorkflowTests.cs)、[PricingWorkflowTests](../../tests/Pricing.IntegrationTests/PricingWorkflowTests.cs) 的公开 `ISender`，以及 [CostingHostTests](../../tests/Costing.IntegrationTests/CostingHostTests.cs)、[PricingHttpTests](../../tests/Pricing.IntegrationTests/PricingHttpTests.cs) 的真实进程/迁移缝。数据库夹具可建立旧版 schema、设锁屏障、拒绝写入或断连，但结论必须从公开查询取得。

| 验证面 | 必须可证伪的结论 |
|---|---|
| 独立升级，两上下文 | 旧 Pending/Running/Failed 首次时间保持 null，旧 Running 总上界 null 不可续；原租约完成和新 epoch 接管仍可用；新行数据库首接时间可返回且重启不变。旧请求省略/显式 0 可重放。 |
| 三个人工入口 | 省略/0 等价；30 天可接受，负数、超限、非整数拒绝；延迟任务到期前不能 Claim；同身份同内容跨重启返回原时间，改延迟/输入冲突；事件仍立即可领取。 |
| 列表与 HTTP | 匿名/无权拒绝；状态/对象筛选、旧 null 排序、同时间唯一排序、越界和大页码；统一状态码及 Int64 字符串；响应没有原始输入和 History。静止数据分页完整，不拿它证明并发快照。 |
| 取消竞争，两上下文 | 用独立 scope/连接屏障验证表中两种锁获胜顺序；取消赢则业务结果不变、CostDelivery 不存在，旧 Complete/Fail/Renew 无权；后续 Claim 不返回 Cancelled，同代次重复取消稳定。 |
| 有界续租 | 在原期限外、新期限内可完成；epoch/attempts 不增加；反复心跳及更换宿主配置不移动本次上界；零实际延长拒绝；过期、Cancelled、错误 epoch 与 legacy 上界 null 均拒绝。 |
| 锁等待与复检 | 在获取行锁前或保存期限后阻塞到原期限过期，确认续租回滚；Complete 在业务写入被阻塞到到期后回滚全部结果/Outbox。使用数据库时钟和可控屏障，不以本机睡眠估计唯一胜者。 |
| 未知提交/重启 | 保存前失败与 COMMIT 响应丢失分开注入；用新 scope 查询确定当前事实，重放不新增任务/历史/Outbox；真实网关完成延迟→列表→取消→宿主重启后仍取消。 |

HTTP 人工操作沿用两个模块的 operator 授权；Claim/Complete/Renew 不开放 HTTP。Int64 和鉴权须检查真实网关、响应 JSON 与 OpenAPI，不能只看适配器测试通过。[CostingModule](../../src/Services/Costing/NexusStackNext.Costing.Endpoints/CostingModule.cs)、[PricingModule](../../src/Services/Pricing/NexusStackNext.Pricing.Endpoints/PricingModule.cs)、[现有 Int64 测试](../../tests/HostIntegration.Tests/HttpInt64ContractTests.cs)、[票据验收](https://github.com/yongpengW/NexusStackNext/issues/48)
