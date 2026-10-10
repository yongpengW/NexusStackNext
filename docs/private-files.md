# 私有文件访问与持久化

私有生成成果的暂存、发布回执与到期恢复协议见[生成成果说明](private-generated-files.md)。这是 #56 的 Files 子票 #149；Pricing 冻结快照与 CSV 生成由 #150、完整跨宿主故障旅程由 #151 承载，交付状态以原生票据为准。

HTTP 中的 Int64（ID、version、epoch、size 等）均返回十进制字符串；请求优先原样回传字符串，精确数字输入继续兼容。见 [HTTP Int64 契约](http-int64-contract.md)。

文件默认私有，上传归属取当前有效会话。只有归属者能够读取元数据、下载和删除；知道文件标识、伪造归属参数或持有根账号都不授予他人文件访问权。其他归属者与不存在的文件统一返回 404，已撤销的会话返回 401。

| 操作 | HTTP |
|---|---|
| 上传原始字节 | `POST /api/files?name=report.xlsx` |
| 查询元数据 | `GET /api/files/{id}/metadata` |
| 下载 | `GET /api/files/{id}` |
| 删除 | `DELETE /api/files/{id}` |
| 查询删除是否完成 | `GET /api/files/{id}/deletion` |

上传成功返回 `fileId`、`name`、`size`；元数据和上传响应都不返回 `StorageKey` 或磁盘路径。下载保留原始字节，不套 JSON，使用附件响应及 `private, no-store`、`nosniff` 响应头。文件名拒绝路径分隔符、相对路径段和控制字符；Content-Type 必须是有效、具体的媒体类型。

## 存储与迁移

配置 `ConnectionStrings__Files`（配置中心键 `ConnectionStrings:Files`）。Files 默认使用 PostgreSQL，拥有独立的 `files` schema、DbContext 和迁移历史；允许与 Identity、Platform 指向同一物理数据库，但三个连接都必须显式配置。

先执行 `pwsh -File scripts/migrate-files.ps1`，再启动平台宿主。脚本只读取私有 `env/platform.dev` 或环境中的 Files 连接；发布产物运行 `dotnet NexusStackNext.PlatformHost.dll migrate-files`。命令不启动 HTTP、配置中心、消息总线或账号播种，重复执行幂等。

普通启动只检查已应用的迁移和数据库可用性；未迁移或不可用会拒绝启动。运行期数据库或磁盘故障使 `/health/ready` 返回 503，`/health/live` 仍只检查进程。

已授权文件的字节暂不可用时，下载返回脱敏的 503；不会将服务端存储故障误报为调用方参数错误。

字节根目录由 `Files__StorageRoot` 配置；部署应使用持久卷或应用发布目录之外的专用目录，并同时备份数据库元数据与字节。开发默认目录为应用目录下的 `file-storage`。不要把它作为静态站点根目录公开。

目录内的 `.nsn-storage-id` 是持久存储身份，备份、迁移时必须与字节一起保留。句柄绑定此身份；挂载丢失或路径被空目录替换时，不得把“找不到文件”认作删除成功。重启后即使新目录可以写入，旧文件的删除仍保持待恢复，直到原存储恢复。此前内存模式产生的无持久归属文件不自动导入，也不自动删除。

仅 Development / Testing 允许显式选择 `Files__Storage__Provider=Memory`。无库平台演示还需同时选择 Identity、Platform 和 Auditing 的 Memory 模式；内存元数据会在重启后丢失，已有磁盘字节不能因此被认作公开文件。

Memory 保存也会生成行审计和最小生命周期事实；元数据、首次删除来源与整批事实在同一临界区提交，失败或取消不留部分状态。
各请求使用独立仓储作用域及共享存储，查询返回快照，版本冲突不会覆盖胜者。配置 RabbitMQ 后宿主自动交付 Files Outbox。
内存中未交付的事实随进程结束丢失；已获 broker 确认的消息可在来源结束后由中央接纳。这仍是开发适配器，不提供持久恢复保证。

## 上传资源限制

`Files__Upload__MaxBytes` 默认 64 MiB；`Files__Upload__MaxConcurrentUploads` 默认每宿主 4 个。必须为正数。上传逐段读写，不要求请求体可定位，不把整份内容缓存在内存中；缺少 Content-Length 也会按实际字节计数。

超限返回 413，名额用完返回 429，不进入无限等待队列；取消或失败会释放名额。文件端点使用同一配置设置 Kestrel 大小限制；外部反向代理仍需单独配置其请求限制。进程能够正常处理失败时会尝试删除部分文件，文件完整写入并提交元数据后才返回 201。

随仓库提供的三份网关路由配置均将 Files 的 `maxRequestBodySize` 设为 67108864（64 MiB）。调整文件大小上限时，同步调整实际部署路由的该字段及外部代理限制；省略路由字段会沿用网关服务器默认值。验收使用实际路由通过网关上传、下载 31 MiB 文件，覆盖超过网关原默认限制的场景。

## 删除与孤儿回收

PostgreSQL 的文件事实有独立容量：默认 100,000 条、总载荷 256 MiB、单条 16 KiB，均与元数据同事务裁决。
一次上传产生登记和存储完成两条事实，不能只提交其中一条；容量拒绝返回 503 / `files.audit_capacity.exhausted`。
未引用字节继续使用下述孤儿回收协议，HTTP 失败不触发不安全的直接删除。
删除申请无法准入时，元数据和首次删除来源均不改变，原文件仍可下载；已经受理的删除若无法提交清除完成事实，
返回 202 并保留持久待办，容量恢复后继续。重复完成不消耗额度。
确认交付不立刻释放容量，须经保留期后的已确认副本清理。Memory 对等容量已通过[四个平台 Memory 事实容量与整批原子恢复](https://github.com/yongpengW/NexusStackNext/issues/80)验收；[可审计策略管理 #101](https://github.com/yongpengW/NexusStackNext/issues/101) 已完成并通过 [PR #102](https://github.com/yongpengW/NexusStackNext/pull/102) 合入 dev，见下文。
迁移和共用规则见 [ADR-0025](adr/0025-context-owned-fact-capacity.md)。

### 来源事实容量策略

Memory / PostgreSQL 已接入所属策略存储。`GET /api/files/audit-capacity` 保留原有业务额度与占用字段，
增加 `policyRevision` 和独立 `controlCapacity`；`PUT` 同路径要求独立资源写权限和当前有效会话，
读权限不授予写权限。请求只有 `requestId`、`expectedPolicyRevision`、三个新额度及固定
`reason=operator-adjustment`，操作者、schema 与事件来源由服务确定。

业务额度满时，扩容使用独立有限控制池；策略、凭据与 `FilesFactCapacityPolicyChangedV1`
在 Files 原有共用写锁或独立本地事务内提交。控制事实携带安全旧/新数值、可信操作者与执行关联，
不包含文件名、字节、句柄或请求正文，不推进 StoredFile 版本或修改行审计字段。
降额不删除已经保留的事实。实际变化推进策略版本；空操作保存有限凭据而不发布 changed 事实。

同请求身份、可信操作者与同内容重放原裁决，先于条件版本比较；异内容、异操作者或旧版本返回
409 / `files.audit_policy.conflict`。非法请求为400，独立控制池满为503 /
`files.audit_policy.control_exhausted`，失败不留下部分策略、凭据或事实。
控制池默认1000个请求、16MiB总载荷与16KiB单请求载荷；不开放控制池自身的在线策略修改。

Memory 可用 `Files:AuditDelivery:MemoryPolicyControl` 在启动时缩小控制池：
`MaxRecords` 为1–1000，`MaxPayloadBytes` 为1–16MiB，`MaxRecordPayloadBytes` 为1–16KiB且不超过总量。
不配置时沿用默认值；关闭维护也不会绕过启动校验，PostgreSQL不使用这组配置覆盖持久额度。
凭据至少保留七天，相关控制事实还必须已经真实确认交付并保留确认副本至少24小时，才可原子清理。
空操作凭据到期可单独清理；待投递与死信保持保留，期限不是强制删除时刻。
安全清理后的请求身份重新按当前条件版本裁决，每次真实变化生成独立事件身份。

新增迁移 `20261004155433_AuditedFactCapacityPolicy` 保留既有业务账本与占用，
只在 `files` schema 创建控制账本、不可变凭据及证据保护；存在已接受控制历史时拒绝破坏性 Down。
当前定向验证包括旧额度/占用升级保留、重复迁移及历史回退拒绝、满额扩容与宿主实例重建后的凭据重放，
宿主实例重建发生在同一测试进程内，不作为独立进程重启证据。
Files 所属控制清理与 `Files:AuditDelivery:PolicyMaintenance` 后台调度均已在工作区接入，
诊断为 `files-policy-cleanup`，参数及期限约束见[共同维护说明](committed-auditing.md)。
来源容量策略的六来源 typed MQ、故障及最终资格以已关闭 #101 的原生完成记录为准；
事实投递专用恢复已由 #103 交付；中央保留与遗漏检查见[覆盖矩阵](committed-audit-coverage.md)。事实归档删除暂缓，调查导出另按票据实施。

### 来源事实条件恢复

[六来源恢复 #103](https://github.com/yongpengW/NexusStackNext/issues/103)已由[PR107](https://github.com/yongpengW/NexusStackNext/pull/107)合入dev，Files 已接入
所属 Memory / PostgreSQL 的调查、双条件恢复、原裁决读取及独立恢复容量接口：

| 操作 | HTTP |
|---|---|
| 有界状态列表 | `GET /api/files/audit-deliveries?state=Pending&limit=50` |
| 单条状态 | `GET /api/files/audit-deliveries/{messageId}` |
| 条件恢复 | `POST /api/files/audit-deliveries/{messageId}/retry` |
| 原恢复裁决 | `GET /api/files/audit-deliveries/recoveries/{requestId}` |
| 恢复池额度与占用 | `GET /api/files/audit-deliveries/recovery-capacity` |

三个状态为 Pending / Delivered / DeadLettered，列表限制一至一百，按发生时刻和消息身份排序。
单条调查不依赖列表位置，六字段状态不包含正文、原始失败或存储位置；恢复代次返回十进制字符串。
仅管理本模块声明的文件生命周期事实与容量策略事实，不开放字节清理或任意消息重投入口。
所有接口要求当前有效会话及各自资源权限，读取不授予恢复权限；这不是文件归属授权。

恢复正文只有稳定 `requestId`、观察到的 `expectedDeadLetteredAt`、`expectedRetryRevision`
及固定 `reason=manual-retry/dependency-restored`；消息身份来自路径，操作者、来源和执行关联来自宿主。
两条件必须共同匹配；相同请求、操作者和同内容在保留期间重放原裁决，不再次开放预算。
成功只表示原事实消息的投递预算重新开放，不表示中央已保存，也不改变文件元数据、版本或字节生命周期。
Memory / PostgreSQL 的真实 HTTP 阶段旅程已验证裁决重放、同停投时刻的旧代次拒绝、
原消息身份／内容／发生时刻保持，以及原文件元数据和下载字节保持；权限／边缘另有实际专项，最终资格见 #103 完成记录。

恢复凭据使用独立有限池，默认1000请求、16MiB UTF-8总量、16KiB单请求，最早保留七天。
`Files:AuditDelivery:MemoryRecoveryControl` 允许缩小开发内存上限，不覆盖 PostgreSQL 持久额度。
Memory 不保证跨进程恢复；PG 使用独立所属连接与短预算事务，不清除调用者的业务工作副本。
新 `20261005051650_ConditionalFactRecovery` 正常增量迁移增加所属账本、凭据及 UPDATE 不可变保护，
已有恢复历史时拒绝破坏性 Down；旧冻结迁移保持。真实 PostgreSQL 阶段验证已经覆盖非空投递状态的
升级保留、重复迁移、模型一致、四个凭据列的改写拒绝、空操作 UPDATE、到期计量释放及清理后的安全 Down / Up。
独立进程重启已通过实际宿主进程退出／重建验证：原恢复裁决可读取并重放，消息状态、元数据和私有下载字节保持。
完整六来源迁移与真实 MQ 资格见 #103 完成记录。

当前 Files 七项专项测试通过：普通事实与容量策略事实分别在 Memory / PostgreSQL 经实际 HTTP 恢复，
原文件元数据和下载字节保持；Memory 有限恢复池满额拒绝且后台到期清理只释放恢复额度；
PostgreSQL 不可变凭据、迁移边界和独立进程重启通过。它们是当时的本地阶段证据；后续整票评审与完整门禁已随 PR107 交付。

`Files:AuditDelivery:RecoveryMaintenance` 显式选择 `IFileAuditDelivery` 的有限清理端口：
默认 Enabled=true、BatchSize=100（一至一千）、Interval=一分钟（一秒至一小时）、
Timeout=三秒（50毫秒至30秒），关闭时仍验证配置。`files-recovery-cleanup` 属于
`auditing-diagnostics`，故障安全降级且下一轮继续，不改变数据库就绪结论。
这组设置只清理到期恢复凭据；文件删除／孤儿字节恢复使用下文的 `Files:Cleanup`，两者互不替代。
六来源最终故障、真实MQ与完整门禁见 #103 完成记录；整体审计验收见[覆盖矩阵](committed-audit-coverage.md)。继续本机，不换机，SignalR 与多机 HA 暂缓。

删除先持久停止提供文件，再尝试清除字节。完成时返回 204；存储暂不可用时返回 202，`Location` 指向删除状态接口，响应中的 `completed=false` 表示还有持久待办。只有归属者可以查询；后台在存储恢复或进程重启后继续处理，完成状态也会保存。重复删除已完成的文件返回 204。404 仅表示不可访问，不能作为物理字节已清除的证明。

恢复配置：`Files__Cleanup__IntervalSeconds=30`、`Files__Cleanup__BatchSize=64`、`Files__Cleanup__RetryDelaySeconds=30`、`Files__Cleanup__OrphanAgeSeconds=3600`。每轮只扫描有界候选；失败删除有持久重试时间。每份文件单独提交，不将整批文件并入一个聚合事务。

首次删除请求的安全执行来源与删除状态同事务保存，重复删除不覆盖。后台恢复每次以
`files.deletion.recover` 记录独立系统操作，并关联原发起人；缺少来源时不根据文件归属者推断。
`deferred` 表示本次仍未确认清除，`completed` 表示已确认；字节移除事实另由 Files Outbox 可靠交付。
后台观察、HTTP 202 与业务事实的区别见[操作日志](operation-logging.md)及[文件事实设计](../src/Services/Files/docs/adr/0004-file-facts-follow-the-committed-lifecycle.md)。

尚未尝试的删除优先于到期重试，两个元数据适配器使用一致的空值排序。独立元数据集必须使用各自独占的字节目录；不要让不同数据库的恢复者操作同一目录。

上传使用独立且永不复用的句柄，在完整字节写入及元数据保存期间持有写入保护。回收只扫描本适配器管理的句柄，先取得同一保护，再与元数据保存共用事务锁确认无引用，并保存退役标记。只有确认退役后才删除字节；数据库不可用或提交结果未知时保留字节并重试。退役句柄不允许再次发布元数据，记录不自动过期。

“HTTP 失败”不能证明“数据库未提交”。故障验收将数据库停在 COMMIT 阶段，再结束宿主进程；原事务若提交，下载必须仍返回原字节。移除回收事务锁后，这个测试会复现元数据已提交但字节被误删，恢复锁后通过。设计取舍见 [ADR-0003](../src/Services/Files/docs/adr/0003-file-recovery-fences-late-publication.md)。

故障验收还覆盖空目录替换、活跃慢速上传、写入中途结束进程、并发删除以及无法清理的单个孤儿。恢复循环不会因一个坏文件而停止处理后续候选；失败会留下日志并继续重试。HTTP 验收覆盖归属与根账号隔离、会话撤销、独立迁移、真实进程重启、数据库故障、超限、上传取消及真实网关的二进制响应。

依据：[PostgreSQL 事务级 advisory locks](https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS)、[EF Core 提交失败与结果未知](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue)。
