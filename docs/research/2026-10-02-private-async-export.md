# Pricing 私有异步导出与下载中心

核验日期：2026-10-02。性质：第一方研究与后续竖切建议，不是已接受 ADR、实施规格或完成声明。按 Matt research 技能调查源码、GitHub 票据与官方资料。本次只新增本文；没有读取环境文件、连接配置或凭据，没有运行构建/测试、连接数据库、访问生产接口或修改 NS/PoS。

源码基线：NS `81831672aefc1f84db3ee5596a445df88ad26bdb`，PoS `6cb62274491ee50600d4103dc01a8a0a1bea114d`；NSN HEAD `34608b6edf088dc8990c26a75d4728ef1292ca6b`，日历 PR #49 的提交。研究期间 root 正在另一个切片修复 Identity API 资源管理权限，不能把未提交改动或在跑的检查当作已经验收。文中的行号是这次读取位置，实施前须重新核对最终 dev。

## 1. 建议与证据强度

**保留“一次提交导出 → 离开页面 → 下载中心看进度 → 下载”的体验，导出的筛选、快照和执行归 Pricing，文件的字节、归属和分发归 Files。** 不引入中央任务表，不让 Files 查询 Pricing 表，不复制 PoS 的正式成本字段与报表反射注册器。

建议在任务管理 [#48](https://github.com/yongpengW/NexusStackNext/issues/48) 及 Costing 批次 [#50](https://github.com/yongpengW/NexusStackNext/issues/50) 之后，实现一条 Pricing 报价快照导出到 Files 的真实跨宿主竖切。#48 只承诺延迟、任务列表、条件取消和受约束续租；#50 明确只交付 JSON 导入与行检查点，文件适配器和私有导出另做。[总规格 #34](https://github.com/yongpengW/NexusStackNext/issues/34) 中“私有文件基础已完成”不能代替“业务异步导出已完成”。

当前推进顺序保持 API 资源管理授权修复 #51 → 任务管理 #48 → Costing 批次 #50 → 私有异步导出；本文仅为后续准备，不插队实施。

有三个必须显式解决的缺口：

1. 当前 Files 没有服务上传契约、幂等上传身份、不可下载的暂存态或发布回执。调用现有 `POST /api/files` 并在 Pricing 存一个 FileId，不能证明重试不重复、取消不漏出成果。
2. 两个数据库不能共同提交。建议 **Files 暂存不可下载 → Pricing 提交发布意图及 Outbox → Files 幂等发布 → Pricing 核对持久回执后完成**。取消只能在发布意图提交前获胜；Publishing 以后不能再把操作报告成“已取消”。
3. 后台使用独立服务身份，Owner 来自接受请求时的有效用户身份。**不保存用户访问令牌/刷新令牌给后台续用。** 下载时仍检查当前会话及归属；根管理员不隐式拥有别人的私有成果。

以上是依据仓库边界作出的方案建议，尚未实测。删除导出模块后，Pricing 的定价与 Files 的普通上传仍应独立可用；接口就是测试面，首个接口必须通过两个真进程调用证明，而不能只有内存替身。[架构不变量](../../AGENTS.md)、[Files 词表](../../src/Services/Files/CONTEXT.md)、[Pricing 词表](../../src/Services/Pricing/CONTEXT.md)。

## 2. NS / PoS 源码证明了什么

### 2.1 值得保留的生产体验

PoS [`CostManageController.ExportCostPriceAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/CostManageController.cs)（173–185 行）从当前用户取 UserId，把筛选序列化进 `ExportExcelRequest`，调用一次 `CreateExportExcelTaskAsync` 后返回。开发者不用自己拼下载记录和消息。

[`AsyncTaskService.CreateExportExcelTaskAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/AsyncTasks/AsyncTaskService.cs)（912–926 行）先插入 `DownloadItem`，再登记 `ExportExcel` 任务。[`ExportExcelEventHandler.HandleAsync`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/EventHandler/ExportExcelEventHandler.cs) 读任务载荷，调用所属导出函数，保存文件并更新下载记录/任务状态。下载中心列表按创建者、日期、状态和名称筛选。保留这种小而完整的使用过程，不保留跨服务共享 Core 的实现。

NS 模板的 [`DownloadService.InitExportTypeMap`](D:/LeoProject/NexusStack/NexusStackBackend/Domain/NexusStack.Core/Services/SystemManagement/DownloadService.cs) 返回的映射仍是 `To Do`（28–35 行）。定向检索 NS C# 中 `ExportExcel/CreateExport/DownloadItem` 找到接口、模型、下载控制器和 Excel 工具，但未找到 PoS 那样的完整异步导出处理器；不能把 PoS 的实际业务链写成 NS 模板已完整具备的链。

### 2.2 不能迁移的具体行为

| 源码事实 | 对 NSN 的含义 |
|---|---|
| PoS 任务载荷只有用户、下载记录、TypeName、QueryData、FileName；真正查询在执行时发生 | 保存筛选文本不等于保存快照。排队期间数据变化后可能导出不同内容；NSN 必须定义冻结时点 |
| PoS 创建下载记录与任务是连续两次服务调用 | 本方法没有证明二者原子；NSN 在 Pricing 一次本地事务持久接受导出及快照，不要求 HTTP 调用者协调两次写入 |
| 处理器 77 行调用 `UploadAsync` 却不 await，然后标记下载 Success、任务 Completed | 必须等待并观察发布结果。特别注意下述同步实现细节，不能夸大为每次都会“上传未结束先成功” |
| PoS `DownloadController` 列表有 CreatedBy 条件，55 行下载却只 `GetByIdAsync(id)`；NS 对应方法 56 行同时限制 id 和 CreatedBy | 列表过滤不保护直接下载。NSN 要对详情、字节、删除及删除状态分别检查归属 |
| 下载把 byte[] 转成逗号分隔十进制字符串 | NSN 已有二进制响应，应沿用流式下载，不回到 JSON 中包装整份字节 |
| PoS 下载列表第二个日期条件仍检查 StartDate，而不是 EndDate | 新列表直接测试仅起始/仅结束/同时指定/无日期，避免照抄该缺陷 |
| 处理器从用户名/邮箱前缀推导 Excel 密码 | 这不是有效的私有访问协议；首版不继承这种可预测的密码 |

上表来源：[PoS 请求 DTO](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Dtos/DownloadCenter/ExportExcelRequest.cs)、[处理器](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/EventHandler/ExportExcelEventHandler.cs)、[PoS 下载控制器](D:/CWChina/CWChinaERP/CWChinaPoS/Host/POS.WebAPI/Controllers/DownloadController.cs)、[NS 下载控制器](D:/LeoProject/NexusStack/NexusStackBackend/Host/NexusStack.WebAPI/Controllers/DownloadController.cs)。这里只是方法级源码结论，没有审计生产全局过滤器，也没有复现公司系统的越权请求。

上传的准确限制：[`AliyunFileStorage.UploadAsync(byte[], string)`](D:/CWChina/CWChinaERP/CWChinaPoS/Infrastructure/FileStroage/AliyunFileStorage.cs)（154–166 行）虽然返回 `async Task<string>`，当前内部是同步 `PutObject`，没有 await。因此正常路径实际先执行上传；明确的问题是**调用方未观察故障 Task，上传抛错仍可能继续标 Success**。换成真正异步适配器后还会增加提前成功窗口。研究未连接 OSS，不报告已复现生产故障。

PoS [`ExportExcelHelper.ExportToExcel`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Excel/ExportStream/ExportExcelHelper.cs) 使用 NPOI `XSSFWorkbook`，在内存中建立工作簿和 MemoryStream，再 `ToArray()`；decimal 经 `Convert.ToDouble` 写单元格。[`DownloadService`](D:/CWChina/CWChinaERP/CWChinaPoS/Domain/POS.Core/Services/SystemManage/DownloadService.cs) 的多个成本导出先 `ToListAsync()` 全量读出。它们证明当前易用接口，不证明超大数据导出的内存上限或精确小数往返。

## 3. NSN 当前能力与真实缺口

### 3.1 Files 已经实现的部分

[`FilesModule`](../../src/Services/Files/NexusStackNext.Files.Endpoints/FilesModule.cs) 的实际端点只有上传、按 ID 元数据/字节、删除、删除状态和文件名验证。上传 owner 来自 `ICurrentUser.UserId`（135 行），不是请求指定；端点使用当前会话检查。下载返回附件字节并设置 `private, no-store`、`nosniff`（145–157 行）。

[`FileService`](../../src/Services/Files/NexusStackNext.Files.Application/FileService.cs) 先完成有界字节写入，再保存元数据（75–121 行）；每次调用内部生成新的 StoredFileId。读取/删除均精确比较 OwnerId。默认上传实际字节上限 64 MiB、每宿主最多 4 份并发，非可定位流也受限。[现有私有文件说明](../private-files.md) 是这些行为的操作契约。

[`EfStoredFileRepository`](../../src/Services/Files/NexusStackNext.Files.Infrastructure/EfStoredFileRepository.cs) 的保存与退役取得同一 storage key 事务锁，保存拒绝退役句柄；[`FileRecovery`](../../src/Services/Files/NexusStackNext.Files.Application/FileRecovery.cs) 按有界待办恢复软删除并回收孤儿。必须保留 [Files ADR-0003](../../src/Services/Files/docs/adr/0003-file-recovery-fences-late-publication.md) 的规则：客户端失败不证明数据库未提交；未知结果不立即删除字节；退役句柄不重新发布。

### 3.2 这些能力现在不存在

核对方式是枚举 Files 的全部源码文件及 `MapPost/MapGet/MapDelete`，再查 Domain 状态和应用方法；不是只按名称猜测：

| 能力 | 基线事实 | 后续所需 |
|---|---|---|
| `NexusStackNext.Files.Contracts` | 没有该工程/目录 | Files 自有的窄 DTO/协议，Pricing 只依赖 Contracts，不引用 Files.Application/Domain/Infrastructure |
| 服务替用户存文件 | 公共上传强制当前用户会话 | 独立认证与授权的服务入口，受信 Producer 及 Owner 归属，不能开放 owner 查询参数替代 |
| 上传幂等 | 每次生成新 fileId，没有外部 UploadId 或摘要去重 | 稳定操作身份、不可变描述、冲突判定、结果查询与长期墓碑 |
| 暂存/发布 | StoredFile 主要是 StorageKey、IsDeleted、清理时间 | Pending/Staged/Published 等明确状态；Staged 不可下载，发布单独裁决 |
| 内容证明 | 元数据返回 size/type/name，无 SHA-256 | Files 对实际收到字节计数并计算摘要，与冻结描述相符才封存 |
| 下载中心列表 | 只有逐个 ID 查询 | Pricing 提供自己的导出状态列表；若要跨业务统一列表，再用事件投影，不能直接 join |
| 到期与清理 | 有已删除恢复和孤儿回收，无 ExpiresAt | 到期立即禁止新下载，后台可靠清理，墓碑阻止晚到上传/发布复活 |
| 外部下载链接 | `IFileUrlProvider` 是 Domain 接口；只有测试替身实现它 | 当前真实适配器只有 LocalDisk，不宣称已有对象存储或临时链接 |

`StoredFile` 的现有字段见 [Domain 源码](../../src/Services/Files/NexusStackNext.Files.Domain/Stored/StoredFile.cs)，接口/替身的真实范围见 [FileStoreSeamTests](../../tests/Files.Domain.Tests/FileStoreSeamTests.cs)。为新功能扩充时，普通用户上传须保持既有契约；旧文件没有明确到期策略，不在迁移中擅自加过期时间。

### 3.3 Pricing 与身份的边界

[`PricingDbContext`](../../src/Services/Pricing/NexusStackNext.Pricing.Infrastructure/PricingDbContext.cs) 当前只有报价、重算、尝试、Inbox 和缓存失效等表；[`PricingModule`](../../src/Services/Pricing/NexusStackNext.Pricing.Endpoints/PricingModule.cs) 没有导出/列表接口。报价查询缓存是最终一致的，导出冻结应直接读 Pricing 主库，不能把 Redis 命中拼成“同一时点快照”。[缓存 ADR](../../src/Services/Pricing/docs/adr/0002-durable-query-cache-invalidation.md)。

当前 Pricing 宿主只验证 JWT 签名、issuer、audience、期限及 Root 声明，没有 Files 那种每请求查询 Identity 当前 SessionVersion 的接线。[Pricing Program](../../src/Hosts/NexusStackNext.PricingHost/Program.cs)、[Pricing ADR-0001](../../src/Services/Pricing/docs/adr/0001-a-minimal-recalculation-reference.md)。不能因为 Identity 或平台宿主能撤销，就称业务宿主即时撤权已完成。Identity 的 [API 资源管理修复 #51](https://github.com/yongpengW/NexusStackNext/issues/51) 也不自动提供跨宿主认证协议。

当前 [`PostgresTaskExecution`](../../src/BuildingBlocks/BuildingBlocks.Infrastructure/Tasks/PostgresTaskExecution.cs) 用数据库时间、epoch 和租约保护 Claim/Complete/Fail/Retry；#48 将扩展取消和续租。导出可沿用其已验证语义，但不能因它是泛型就直接塞进新表：要核对它固定的任务表、状态机和调用者边界。Publishing 的外部发布承诺不能被普通 Failed/Cancelled 路径覆盖。

## 4. Pricing 有界冻结：先定义导出到底是什么

以下均为建议，容量值是首版保护上限，不是测量得到的 SLO。

### 4.1 接受一次明确意图

建议 `POST /api/pricing/exports` 接受非空 RequestId、固定格式/列集版本和白名单筛选。Owner、来源上下文、文件扩展名不能由用户冒充。首版只导出 Pricing 自己的演示报价字段：ItemId、Cost、FeeRate、BreakEvenPrice、InputRevision、CalculatedRevision、CostingRevision、Version 和计算新旧状态；**不回查 Costing、不拼公司产品/月份/税费字段**。

可支持有界 ItemIds（去重排序，最多 5000）与 `Current/Stale/Uncalculated/All` 枚举筛选；不给任意 SQL、反射类型名、表达式或动态列。首版行数最多 5000、请求实际字节最多 256 KiB，固定排序 ItemId。查询 `limit + 1`，超过上限明确拒绝，不默默截断；空集合返回稳定 `pricing.export_empty` 400，不留下永久等待记录。若后续需要十万行，先单独测快照写放大、临时盘和格式容量。

接受事务在 Pricing 内完成：按 `(OwnerId, RequestId)` 串行化重复请求 → 规范化并核对 RequestHash → 从自身报价表取得有界一致视图 → 写 ExportJob 及不可变 ExportRows → 提交。只创建一个导出聚合及其子行/执行元数据，不修改这些 PriceQuote 聚合。保存 Owner、AcceptedAt、SnapshotTakenAt、筛选语义版本、固定列/格式版本、行数及 SnapshotHash。完成后返回 202、ExportId 和可查询 Location。

PostgreSQL `REPEATABLE READ` 中连续读取使用同一事务快照；普通 Read Committed 的多条 SELECT 不能据此承诺同一视图。建议一个短事务内冻结、声明 statement/lock/transaction 时间预算，写完就释放，不保持快照事务去生成 ZIP 或上传 HTTP。[PostgreSQL 官方隔离级别](https://www.postgresql.org/docs/current/transaction-iso.html#XACT-REPEATABLE-READ)。单条 `INSERT … SELECT` 的备选也须同时处理身份/超限/审计，不能用“是 SQL”回避不变量。

这里 SnapshotTakenAt 是 Pricing 冻结时的观测标记，不是整个 Costing → Pricing 链全局最新证明；异步上游尚未到达时只能导出 Pricing 当时接受的事实。结果过时则记录 Stale 与两个 revision，不偷偷重算后改写同一份快照。[Costing 事件 ADR](../../src/Services/Costing/docs/adr/0001-cost-results-as-versioned-facts.md)。

### 4.2 两种摘要，两个不同问题

| 身份/摘要 | 用途 |
|---|---|
| `(OwnerId, RequestId)` | 同一次用户意图；重试不能重新读取活表、重置创建时间或生成第二份工作 |
| 版本化 RequestHash | 规范化筛选、格式、列集等请求内容；数值/属性顺序等价时相同，业务含义改变返回 409 |
| ExportId + SnapshotHash | 第一次接受的所有冻结行、顺序、版本和精确值；用于恢复和内容追踪，不把请求摘要误当输出摘要 |
| CandidateUploadId | 某一次生成候选文件的稳定身份；在首次上传前持久保存，与业务 RequestId 分开 |
| Actual SHA-256 + Size | Files 对实际字节的完整性证明；不单独作为跨 Owner 的全局文件共享键 |
| PublicationId | 选定唯一候选后形成的不可变发布意图，重复交付必须得到同一回执 |

并发相同请求只能有一个已提交 ExportJob；数据库 COMMIT 响应未知时先用稳定身份核对持久结果，不能马上另建新身份重做查询。EF 官方明确指出提交阶段连接失败会留下未知结果，建议稳定键与状态验证；这也是本仓库 Files 恢复已有的前提。[EF Core 提交失败与幂等](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue)。

进度的分母是冻结行数。生成可分段读快照并汇报处理行数，但 CSV/XLSX 文件未完成封存之前，不能把“写了若干行”当成跨进程可接续的文件检查点。首版允许崩溃后重新渲染有界快照；明确保留任务历史、快照和阶段，不宣称能从任意 ZIP 偏移恢复。#50 的数据库行检查点不能直接移植成 ZIP 续写协议。

## 5. 跨数据库文件发布协议

### 5.1 为什么需要单独的 Publishing

“上传完 → 标 Completed”在 HTTP 响应丢失时会重复上传；“先标 Completed → 上传”会出现没有成果的成功任务；“取消时直接删文件”与迟到上传竞争时还会复活成果。两个上下文各自拥有数据库，不能靠一个本地 TransactionScope 使这些步骤原子。

建议区分**可取消的生成阶段**与**已经提交的发布承诺**：

| Pricing 阶段 | 下载可用性 | 允许的操作 |
|---|---|---|
| Pending / Generating / Retry | 无可下载成果 | 按观察 epoch 取消、有效执行者续租、条件重试；所有写入受所属数据库裁决 |
| Publishing | 可能尚未发布，状态必须可查询 | 持久重试同一 PublicationId；取消返回 409 `pricing.export_publication_committed` |
| Succeeded | 曾收到 Files 的正式发布回执 | 可下载性另看文件状态；用户可删除成果，不把删除称作撤销已完成任务 |
| Cancelled / Failed | 无成功成果承诺 | 保留快照/错误/尝试；Failed 在没有发布承诺时可条件重试；Cancelled 重新导出使用新 RequestId |

Publishing 不是“快要成功所以当成功”，也不能因网络异常直接转换成“发布失败、可以另建文件”。它保留有限重试预算、下次尝试与错误码；预算用完显示需要人工恢复的发布待办。恢复只能重送原意图或查询回执，不能重新接受活表数据。到暂存期限仍未发布，由 Files 给出明确 Expired 终态后才能结束该意图。

**这是建议的取消语义取舍，不是声称用户已经批准了某个状态机。** 它让取消与跨上下文发布有一个可解释的线性化点。若将来要求 Publishing 后仍可“取消”，应另做持久撤销/删除协议并向用户呈现 CancelRequested，等待 Files 确认不可再发布后才 Completed；不能只加一个取消令牌或本地布尔值。

### 5.2 建议步骤

1. Pricing 领取工作，使用 #48 已验收的数据库时间、ExecutionEpoch、有限租约及最大执行时长。生成读取冻结行，不持有业务事务，不持久化用户 JWT。临时文件限制总大小/并发/磁盘配额，不用 byte[] 同时复制数份。
2. 生成结束计算摘要与长度；在仍拥有有效执行权时持久登记 CandidateUploadId、Owner、SourceExportId、SnapshotHash、格式版本、name/type、size/hash。候选属于这一份导出；新尝试可新建候选，但不能改旧候选描述。
3. 经 Files 服务契约按该 UploadId 暂存字节。Files 自己验证实际长度与摘要，持久封存后返回 Staged 回执；普通用户、根管理员都不能下载 Staged 内容。上传错误/响应丢失先按 UploadId 查询，无结果才能按同一描述重送。
4. Pricing 核对 Staged 回执，开启短事务锁住 ExportJob，再检查状态、epoch、租约与取消。原子保存唯一 SelectedUploadId、PublicationId、发布描述及 Outbox；进入 Publishing。**这是取消的最后裁决点**。
5. Pricing 所属发布适配器从持久 Outbox 交付同一发布意图。Files 验证 Producer、UploadId、所有不可变描述和未过期状态，原子将该 StoredFile 从 Staged 变成 Published，并保留 PublicationId 回执。重复相同内容返回相同 FileId；不同内容拒绝 409。
6. Pricing 通过成功响应或同一 PublicationId 的查询得到持久回执，核对 Owner、SourceExportId、hash/size 后保存 FileId、PublishedAt 与 Succeeded。若本地提交结果未知，再查询自身 ExportId，不生成第二个文件。事件通知可在这个本地事务登记；实时通知不决定完成事实。

Outbox 只携带发布描述及候选身份，不能装几十 MiB 二进制。字节走有界 HTTP。当前 RabbitMQ 适配器不等于“任何出站 HTTP 都已有事务性投递”；发布待办/回执恢复需在 Pricing 实现并通过进程故障测试证明。

建议 Files 的 `(Producer, CandidateUploadId)` 和 `(Producer, PublicationId)` 作用域都受认证 Producer 约束。一个 StoredFile 聚合承载自己的暂存、发布和删除状态；请求去重/回执是该聚合的执行元数据，尽可能同一事务保存，不能先写“去重成功”再另写文件。跨文件不在一个事务批量推进。

**Files 已 Published、Pricing 仍 Publishing 的窗口是真实存在的。** 建议 Files 从 Published 开始允许有效 Owner 下载；它不回查 Pricing 任务状态。Pricing 列表在尚未核对回执时继续显示“发布中”，不宣称整个链同时更新；普通流程可等状态确认后才显示下载按钮。即使 Owner 已通过其他授权路径得到 FileId 并提前下载，这也是已提交发布意图的合法成果，不是被取消工作泄露的文件。若产品要求“两边都确认后才可见”，需要另增最终可见协议，不能用隐藏按钮声称达到这一保证。

**对账由 Pricing 的持久发布恢复者负责**：有界扫描 Publishing 与未完成 Outbox，按 PublicationId 查询 Files 持久回执，验证描述后推进本地完成；Files 只回答自身发布事实及当前可用性，不访问 Pricing 表。通知事件可加速，轮询回执提供重启与丢通知后的恢复。这里是出站义务的恢复机制，不是运行临时后台 Task 后任其遗忘。

回执应分开表示 `PublishedAt/FileId/内容摘要` 与 `Availability`。若回执到达前文件已经到期或被 Owner 删除，历史上确已发布仍可使 Pricing 记录完成事实，同时列表明确“已到期/已删除”，不提供可下载承诺。若根本没有发布就到暂存期限，结果是 PublicationExpired；若字节盘故障，结果是暂不可用，不是“文件从未产生”。三者不能合并成一次失败重跑。

### 5.3 旧执行者与格式不确定性

旧 epoch 上传的候选最多成为 Staged 孤儿；它无法通过 Pricing 的发布意图提交屏障，因此不能下载。已提交 Outbox 的发布是合法的业务承诺，即使原生成进程已经退出，恢复者仍应完成它；不能把 Outbox 重投误判成“旧执行者在改新结果”。Pricing 后续完成写入仍须匹配既有 PublicationId/选定候选，不能接纳其他回执。

XLSX 是 ZIP 包，生成库和元数据时间等可能使同一逻辑数据得到不同字节。本文没有实测任何库的字节确定性，**不假定重新生成必然等于原 hash**。可恢复边界是已经由 Files 封存的候选：

- 同候选描述的上传重试必须用相同字节；Files 对冲突内容拒绝，不覆盖。
- 生成进程退出、临时文件丢失后先查询候选。Staged 已存在则用原回执继续；未封存且无法再生相同字节，可在新有效 epoch 下登记新候选，旧候选只清理。
- 一旦选定候选并进入 Publishing，只发布该候选，不重新渲染、不换 FileId、不从活表补做。Files 暂不可用时等待恢复；已丢失字节则是明确存储故障，不能静默替换“同一成果”。

CSV 可以把排序、换行、编码、精确数值与全部元数据固定而获得确定性输出，但仍需相同的持久回执协议。格式确定性不是远程提交幂等性的替代品。

## 6. 两个真实宿主、Contracts 与授权

### 6.1 窄契约与适配器位置

建议新增 Files.Contracts，先只定义这条链真正需要的描述和结果；名称供设计时选择，以下不是现有 API：

| 候选操作 | 必须表达的结果 |
|---|---|
| `StageGeneratedFile` / `ReadGeneratedUpload` | UploadId、不可变描述、Staged/Expired/Rejected、Files 计算的 hash/size；不返回 StorageKey |
| `PublishGeneratedFile` / `ReadPublication` | 同一 PublicationId 的 Published/FileId/Owner/PublishedAt，或明确冲突/过期；不得把超时改写成 NotFound |
| `ReadFileAvailability` | 所有权/来源受限的 Available/Expired/Deleted/StorageUnavailable；不能因 404 认定物理删除完成 |

Pricing.Application 定义自己需要的文件发布端口，返回自己的业务结果；Pricing.Infrastructure 的 HTTP 适配器引用 Files.Contracts 完成传输。PlatformHost 的 Files.Endpoints 组装接收端。Pricing 不注入 Files.FileService，不共享根目录，也不引用它的 Domain/DbContext。这里缝上会变化的是**远端文件服务的传输/故障与生命周期**，不是为了每个类都多写一个接口。

首个真实适配器必须用独立 PricingHost 与 PlatformHost 进程、两个数据库连接、真正 HTTP 序列化、有限超时和取消测试；测试替身只用于确定性错误支路。不要先声称有第二个对象存储实现、消息发布适配器或万能报告引擎。

### 6.2 服务身份与 Owner 是两个主体

建议首次采用框架支持的内部 HTTPS 客户端证书认证：证书映射为稳定 Producer（如 pricing），授权仅覆盖自己的生成文件命名空间、暂存/发布/回执查询。该服务权限不包括下载任意用户私有文件、枚举其他 Producer 或改 owner。密钥由部署凭据注入，测试使用临时测试证书；本文没有创建或修改任何证书/secret。

ASP.NET Core 的证书认证发生在 TLS 层，需要验证证书与应用主体的映射；反向代理终止 TLS 时，证书转发只有在可信代理链中才成立。建议先在私有服务网络端到端验证，不信任公网客户端传来的证书头。[Microsoft 证书认证文档](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/certauth?view=aspnetcore-10.0)。这是一项待实现部署/适配器选择，不是已部署认证能力。

服务端点使用独立认证 scheme/policy，放在公共 `/api/files` 之外；网关不得为这些内部操作提供匿名/普通用户通配路由。内部 TLS 监听端口不发布到宿主公网，符合“边缘是唯一外部入口”；单机可在回环/容器私网完成这条验证，不要求先搭多机 HA。实际路由与普通用户令牌、伪造 header、错误 Producer/过期证书都需负向测试。

如果后续有标准 OAuth 授权服务器，应评估其短期 client-credentials 工作负载令牌和 audience/scope，使用现有标准实现，不自造签名请求协议。OAuth 安全 BCP 推荐可行时采用非对称客户端认证；这不能被引用成“NSN 已经有 OAuth server”。[RFC 9700 §2.5](https://www.rfc-editor.org/rfc/rfc9700.html#section-2.5)。

Owner 则来自首次接受请求的已验证用户 sub（当前部署的固定 Identity issuer 范围），持久保存在 ExportJob。文件 owner 由受限 Producer 从该已接受事实带来，不从任意浏览器参数复制；不把 Producer 名当作 Owner。后续更换服务证书不改变 owner 或上传幂等身份。

### 6.3 人的访问必须单独校验

首版如果沿用 Pricing 根操作者策略，要明确写出这个能力范围，不能顺便声称普通业务角色与即时撤权已经完成。最终目标的普通用户授权应由独立 Identity.Contracts 的远端当前会话/权限协议或经过验证的事件投影补齐；目前该 Contracts 工程也不存在，不能只注册一个同名接口就视为接通。

无论谁能创建导出，下载都使用 **当前有效会话 + Owner 精确相等 + Published + 未删除/未过期**；其他用户和其他根管理员不因知道 FileId 得到字节。取消、重试、状态列表同样先限制所有者；若业务确实需要管理员管理他人导出，新增可审计的明确权限，不顺带授予下载内容权。

后台授权是首次接受时的有限导出委托，用户令牌过期/普通登出不应令已接受的计算突然丢失；用户再次登录后可查同一工作。失效会话不得下载。若业务要求“禁用账号/撤销权限也撤销尚未完成的导出”，必须另行定义持久撤销事件与 Publishing 截止点；不能靠后台临时拿旧 JWT 请求 Files 来偶然决定。

## 7. 下载中心、到期与回收

### 7.1 一个页面不需要一个中央业务库

首个下载中心列表由 Pricing 提供 `GET /api/pricing/exports`：只查询本人、固定状态/日期筛选、默认 50 最大 200，稳定游标排序 `(AcceptedAt, ExportId)`。返回 ExportId、名称、格式、冻结行数/已处理数、阶段、代次、时间、稳定错误码，以及成功后可访问的 FileId；不返回整份筛选载荷、文件路径或敏感内部异常。

页面可以显示“生成中 / 发布中 / 已完成 / 失败 / 已取消”与独立的“可下载 / 已到期 / 已删除 / 暂不可用”。Succeeded 是曾完成的业务事实，存储后来掉线不应把它伪装成生成失败；也不能只根据 Succeeded 永远显示下载可用。Files 的实际下载端点是最终裁决者。

以后有第二个业务导出消费者，再用明确的契约事件构建统一下载目录投影，按来源回查详情；幂等事件身份、乱序更新和所有者都要处理。不要让 Files 为展示“生成 60%”而读取 Pricing 任务表，也不要为一个页面提前制造跨域通用执行器。

### 7.2 不同时间控制不同义务

建议初始保护：暂存候选最多保留 24 小时，发布后可下载 7 天；这些值是待验收配置而不是容量承诺。均在首次状态转换由 Files 数据库时间确定，重复上传/发布不得续期。Pricing 发布前可读出 StageExpiresAt，过近时不提交承诺；超出期限后的唯一结论由 Files 持久状态决定，不能本地猜测“应该已经没了”。

- **Staged 到期**：同一 StoredFile 事务把状态推进为 Expired/Deleting，阻止迟到封存/发布；随后走既有可靠清理。没有可下载成果被收走。
- **Published 到期**：新下载请求立即按 ExpiresAt 拒绝，即使清理工作进程停止也不能继续放行；后台每份文件独立删除并重试。已经打开的有界下载流可能继续完成，应明确此语义，不能承诺到期瞬间撤回已发送字节。
- **Owner 主动删除**：先持久禁止提供，再清字节；沿用 202/204 和删除状态。Published 回执保留历史事实，后来的发布重投返回原 Published/Deleted 状态，不重新创建文件。
- **孤儿字节**：只使用 Files 现有写入保护、事务锁及退役协议；年龄仅筛候选。服务上传的 Pending/Staged 元数据和活跃字节也要被保护，不能被旧扫描误认无引用。
- **Pricing 临时文件**：仅清理本适配器管理的、无活跃写入者的候选目录；临时路径不跨上下文，也不成为下载地址。已选定发布候选只依赖 Files 的持久内容，不依赖临时盘残留。
- **幂等墓碑与请求快照**：首版不自动删除 RequestId、UploadId、PublicationId 的内容摘要和终态；字节到期不意味着可以复用身份。快照清理必须晚于所有恢复义务，并保留不会重新执行的最小记录，另订保留政策。

下载中心应能查询本人文件“已到期/已删”而不是把这些都说成 404；对其他人仍统一 404。到期策略新增列及迁移需要独立命令、重复运行、重启、旧普通文件不被误清理的验证。[现有 Files 持久化与恢复说明](../private-files.md)。

**同一 ExportId/TaskId 在成果到期或删除后重试，不允许生成不同成果。** 已选定 PublicationId 保留同一 FileId/摘要/终态；普通“重试”只能恢复这个发布义务。用户需要再次下载已清理的内容时，应发起新的 RequestId，并由 API 明确这是一次新的冻结/导出；首版不增加“偷偷用旧 TaskId 导出当前最新数据”或“后台重置过期时间”。若将来支持按旧快照重导，也须建立新工作身份并明确关联原 ExportId。

## 8. CSV / XLSX：格式不是可靠性协议

建议先用固定列 CSV 打通全链可靠性，在同一业务接口增补 XLSX；**仅 CSV 验收不能宣称已补齐 PoS 的 Excel 体验**。最终若面向人工使用，以 XLSX 为默认更合适；两种格式共用已冻结数据与文件生命周期，格式及版本是请求身份的一部分。

| 项目 | CSV | XLSX |
|---|---|---|
| 适合首个竖切 | 固定编码/行序/换行，容易精确比较字节和验证失败恢复 | 更贴近 PoS 人工下载，必须证明完整包、单元格类型、恢复与资源上限 |
| 首版结构 | UTF-8，是否 BOM 在格式版本固定；固定表头、CRLF；逗号/引号/换行按 RFC 转义 | 单工作表固定列、固定样式；不接受用户模板、公式、宏、外链或自动超链接 |
| 精确值 | Guid/Int64 十进制/decimal invariant 文本，无科学计数和 double 中转 | 标识、revision、epoch 强制文本单元格；金额精确表示见下文 |
| 资源界限建议 | 最多 5000 行、最终实际字节 32 MiB、每宿主生成并发 1 | 相同行/最终字节限制，额外限制展开 XML 总字节、entry 数和每 cell 文本；必须关闭包后验证 |
| 失败时 | 丢弃未封存候选，保留输入快照和尝试 | 不能发布半份 ZIP；不能把文件长度非零当完整 XLSX |

RFC 4180 描述 CSV 引号/逗号/换行的共同格式及 MIME 注册，本身是 Informational，并不保证某个电子表格软件的自动类型转换结果。[RFC 4180](https://www.rfc-editor.org/rfc/rfc4180)。Excel 的工作表上限为 1,048,576 行、16,384 列，单元格 32,767 字符，数字精度为 15 位；应用的 5000 行/32 MiB 限制远小于文件格式上限，不应混称。[Microsoft Excel 限制](https://support.microsoft.com/en-us/excel/excel-specifications-and-limits)。

### 8.1 精确数据与公式注入

所有 Int64 必须从 long 直接转十进制字符串，继承 [HTTP Int64 契约](../http-int64-contract.md)；不能先经 JavaScript Number、double 或 Excel 数字单元格。金额在 Pricing 已是 decimal(18,4)，输出用固定 invariant 精度，不能照搬 PoS 的 Convert.ToDouble。

XLSX 默认建议对要求严格往返的金额提供**文本形式的精确列**；是否另加可求和的数字便利列须在格式契约明确。若只写数字列，应给出本样板全部取值在目标 Excel 中四位小数往返的实测证据，不能仅因 .NET 写入是 decimal 就承诺接收端精确。CSV 在文件层能保留精确文本，但 Excel 双击打开的自动类型推断可能改变标识/数值；不能为强制文本使用 `="..."` 公式。这个使用差异要在下载说明中简洁表达。

CSV 正确加引号不等于防公式执行。OWASP 说明 `= + - @`、控制字符及部分全角变体可能触发公式，分隔符/引号也可能改变新单元格起点；保存再打开会使某些转义失效，没有对所有软件和下游都通用的净化方法。[OWASP CSV Injection](https://owasp.org/www-community/attacks/CSV_Injection)。

首个 Pricing 报表固定列只有受验证的 Guid、非负数值、时间和枚举，表头固定，用户文件名不写入 cell；无需把任意业务文本加入导出以“证明通用”。如果增补用户文本，优先 XLSX 显式字符串类型、不生成 formula 元素；CSV 要选择并测试明确的人工查看/机器导入契约（转义改变原文时必须有说明），必要时拒绝危险值。不得同时声称“任意原文不变”和“所有电子表格都安全”。Open XML 明确区分 Number/String/InlineString 等单元格类型。[CellValues 文档](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.cellvalues?view=openxml-3.0.1)。

### 8.2 ZIP、内存与许可

导出端首版不读取用户 ZIP/XLSX 模板，所以不需要为了导出实现任意压缩包解析；自己的 XLSX 验证器仍应有 entry/展开字节上限，不依赖压缩后的 32 MiB 防止内存膨胀。若未来接入文件导入，单独验证展开大小、文件数、嵌套包、路径、外链及公式，不由本导出切片顺便开放。

建议 XLSX 首选评估 Open XML SDK 的顺序写入到有界临时文件，避免整个 Sheet DOM 和 byte[] 多份复制；ClosedXML 可作为更易用但必须在本数据上测量内存的候选。**不能说选 SAX 就实现恒定内存**：微软文档说明 DOM 与 SAX 的不同，SDK 当前 README 还明确列出 .NET ZIP 包的流式限制和工作集风险。[大工作表与 SAX](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-parse-and-read-a-large-spreadsheet)、[OpenXmlWriter API](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.openxmlwriter.create?view=openxml-3.0.1)、[SDK Known Issues](https://github.com/dotnet/Open-XML-SDK/blob/main/README.md#known-issues)。容量必须通过 Windows/Linux 实际工作集、临时盘及超限测试后才宣称。

许可核验仅陈述上游条款，不表示已批准引入包：

- [Open XML SDK LICENSE](https://github.com/dotnet/Open-XML-SDK/blob/main/LICENSE)：MIT；拟用其生成适配器时锁定具体版本并核对传递依赖及许可证通知。
- [ClosedXML LICENSE](https://raw.githubusercontent.com/ClosedXML/ClosedXML/develop/LICENSE)：MIT；许可证合适不代表当前场景性能已经证明。
- [EPPlus 官方许可](https://epplussoftware.com/LicenseOverview)：5 起免费部分为 Polyform Noncommercial，商业环境需要商业许可。PoS 项目引用 EPPlus 8.5.1 不成为 NSN 的选型依据；按 [系统 ADR-0008](../adr/0008-no-commercial-dependencies.md) 不引入这一商业使用依赖。

本研究没有安装包、选择最终版本、测性能或对公司已有授权作结论。

## 9. 建议的最小交付边界

第一条可独立验收竖切：**固定列 Pricing CSV → 有界冻结 → 所属任务 → Files 受认证暂存/发布 → 本人状态列表与下载 → 到期/删除恢复**。可靠性与授权必须一起交付，不能把“私有”和“不提前成功”留到后续补丁。随后在同一接口增加 XLSX 并验收精度、格式、许可与资源限制，才完成 Excel 体验项。

不先做多模板设计器、任意列表达式、报表 SQL、中央任务/进度服务、公共下载链接、跨域整表查询或报表库抽象。没有第二个真实消费者前，快照/渲染/发布协调仍留在 Pricing；Files.Contracts 是既有上下文边界协议，不是把唯一消费者的实现提前移到 BuildingBlocks。

建议验收先约定这几项可观察语义，再实施：冻结在首次接受时；空数据/超限拒绝而不截断；Publishing 是取消截止点；文件过期与任务完成是两个状态；默认仅 Owner 下载；普通业务角色/即时撤权是明确前置或显式受限范围。它们影响 HTTP 契约，不能到测试写完后再解释。

## 10. 必须经过公开边界的故障矩阵

数据库只用于故障注入/竞争屏障；业务结论走 HTTP/ISender、文件字节和重启后查询。下面是待实现验收，不是这次跑过的测试。

| 场景/注入点 | 必须观察到的结论 |
|---|---|
| 相同 RequestId 两个请求并发、接受 COMMIT 后丢响应 | 只有一个 ExportJob、一份冻结快照；重试保留首次时间，内容改变 409 |
| 第一次快照后修改报价/Costing 新事件到达 | 重试/进程恢复仍输出原快照；新 RequestId 才看到新版本；不受 Redis 旧命中影响 |
| 精确 0/1/5000/5001 行，请求/输出超限，chunked | 非空上限有真对象；空/超限明确拒绝，无可执行半份快照或下载半文件 |
| 生成中磁盘满、流取消、进程退出 | 不 Succeeded，名额释放；后续从冻结快照恢复，其他导出不被单个坏文件永久阻塞 |
| Staging 字节中断、元数据保存失败/结果未知 | 用户不能下载 Staged；查询 UploadId 解决未知结果；不误删可能已登记字节 |
| 相同 UploadId 内容/owner/长度/摘要被改变 | 拒绝 409；跨 Producer 不可探测或覆盖；Files 以实际读到字节判定 |
| 暂存完成后杀 Pricing，或租约到期被另一进程接管 | 新执行者找到既有候选或生成新候选；旧 epoch 不能提交发布意图，只有选定候选发布 |
| Cancel 与发布意图提交竞争 | Cancel 赢则没有有效发布 Outbox、无可下载成果；Publishing 赢则取消 409，不能先回取消成功再出现文件 |
| Outbox 已提交但未发、发送后无 ACK/HTTP 响应 | 重启可恢复；只使用同一 PublicationId；服务证书轮换不改变身份或所有者 |
| Files 发布 COMMIT 后杀进程/断开响应 | 重投/回执查询返回相同 fileId；Pricing 未证实前保持 Publishing，不另建文件、不补偿误删 |
| Files 成功回执后 Pricing 完成 COMMIT 未知 | 按 ExportId/PublicationId 核对；最终一个成果/完成事实，错误回执不推进 |
| Files 已 Published，Pricing 未收回执时下载或文件先被删除/到期 | 合法 Owner 可按 Files 当前状态访问；对账保留发布历史并分别展示可用性，不用原 TaskId 生成替代成果 |
| Files 暂不可用、数据库不可用、内容盘替换 | 503/持久重试，无成功假象；恢复后继续；沿用 storage identity，不能将“空目录”当已清理 |
| 发布与 Stage 过期清理竞争 | 只有一个持久结论；过期赢则晚到发布拒绝，发布赢则保留至公布的 ExpiresAt |
| 成果删除/过期后重放上传与发布 | 不复活、不重置到期时间；同一身份仍指向原墓碑；删除完成单独可查 |
| 清理者停止后到 ExpiresAt | 新下载立即禁止；后台恢复才完成字节删除；列表不误报“仍可下载” |
| 匿名、普通其他用户、其他根身份、撤销会话直接猜 ID | 元数据/字节/删除/状态都受保护；他人 404，失效会话 401；列表不能成为唯一授权检查 |
| 普通用户令牌/伪造证书头/错误或过期服务证书打内部路由 | 不得暂存/发布；真实网关没有内部通配路由；没有生产 AllowAnyCertificate 式后门 |
| 用户提交后登出、后台生成完成、用户重新登录 | 后台不依赖过期用户 JWT；旧会话不能下载，新合法会话能查原工作和本人文件 |
| `long.MaxValue`、四位 decimal 边界、引号/换行/危险文本 | HTTP Int64 保持字符串；导出精确值不经 double；CSV 正确转义且不生成公式，XLSX cell 类型/无 formula 断言 |
| 格式写入末尾失败、ZIP 截断、并发最大生成负载 | 不封存半文件；实际字节/临时盘/工作集有界；XLSX 不以“扩展名正确”当作验收 |
| 全新独立迁移、旧 Files/Pricing 数据升级、两个宿主重启 | 旧文件授权/删除行为不变；普通启动不迁移；旧文件不被无依据加到期时间 |

最终旅程必须经过真实网关及独立 Pricing/Platform 进程：授权接受快照 → 查进度 → 注入一次上传或发布中断 → 重启 → 发布完成 → 本人二进制下载并核对内容 → 他人拒绝 → 到期/删除恢复。保留 Costing → Pricing、任务取消/续租、缓存失效、Files 未知提交与孤儿回收回归。

每轮仍执行 build → 串行 tests → format、凭据/票据检查、模板生成、Windows 与 Linux CI、Standards / Spec 独立评审。本文没有执行这些门禁；上表不构成测试通过记录，更不构成多机 HA、生产负载、对象存储或备份恢复验收。

## 11. 实施前仍需收敛的选择

- 采用本文建议的 Publishing 截止点，还是做更昂贵的发布后撤销协议；应在票据/ADR 明写，不能沿用普通计算取消语义却偷偷留例外。
- 服务认证先用受限 mTLS 还是接入既有标准工作负载令牌；两者当前都未实现，必须选一个真接线并测试，不能用用户 JWT/固定 root 冒充服务。
- 第一条导出是否保持根操作者范围；要向普通业务用户开放，先补远端当前会话与资源授权契约。Files 的 Owner 校验始终保留。
- CSV 的人工/机器消费语义，以及 XLSX 精确金额列的类型；小数文件表示正确不代表 Excel 用户操作后仍完全等价。
- 暂存/发布期限、行数/字节上限及临时盘配额应以首个样板测量收敛；本文的建议值不能直接写成“已经支持”的容量指标。

这些没有阻止写一条完整规格，但都是验收前要落实的设计。研究没有发现必须读取公司生产数据或移植正式成本模型才能完成这条样板的前提。
