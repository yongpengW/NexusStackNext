# Identity 持久化运行

Identity 默认使用 PostgreSQL，数据与迁移历史均在 `identity` schema。
同一宿主中的 Platform 也需独立连接配置与迁移，见 [全局设置持久化](platform-settings.md)。
Files 的元数据也需独立连接配置与迁移，见 [私有文件](private-files.md)。
Auditing 与 Scheduling 的迁移和运行约定分别见 [持久审计](committed-auditing.md) 与 [持久调度](durable-scheduling.md)。五个平台模块各自拥有 schema 和迁移；部署验收仍需使用目标环境进行。

## 配置与启动

在私有 `env/platform.dev` 或部署环境配置 `ConnectionStrings__Identity`，值不要进入仓库、命令参数或日志。
宿主也可从配置中心的 `ConnectionStrings:Identity` 读取；迁移命令仅从环境读取，不依赖配置中心。
JWT 密钥与根账号仍按 [环境配置](../env/README.md) 提供。

```powershell
# 本机读取 env/platform.dev；先迁移，再启动。
pwsh -File scripts/migrate-identity.ps1
pwsh -File scripts/run-host.ps1 platform
```

发布后的迁移入口：`dotnet NexusStackNext.PlatformHost.dll migrate-identity`。
它不启动 HTTP、RabbitMQ、配置中心或根账号播种；成功退出码为 0，失败非 0。
重复运行只应用尚未执行的迁移。普通 HTTP 启动仅检查迁移齐全及用户表可读，10 秒内检查失败就退出。
运行期 `/health/ready` 检查数据库连通性；数据库掉线返回 503，`/health/live` 不查数据库。

部署先备份并检查迁移脚本，使用有 schema 修改权限的部署账号运行迁移；运行账号只给必需的数据访问权限。
2026-10-02 开发阶段确认没有历史数据后，七个上下文的迁移统一重置。`InitialIdentity` 一次创建完整模型，包括会话版本和用户、角色、API 资源、菜单树的创建与修改审计字段。该初始迁移用于空 schema，不是旧迁移链的增量升级：已有开发 schema 需先清理，再初始化；不能仅清空迁移历史并保留旧表。只处置确认可重建的 NSN schema，保留共用数据库中配置中心或其他系统的数据。旧内存模式的数据不会自动导入。
生成审阅用 SQL 可使用与项目相同版本的 EF 工具；设计时工厂使用无凭据占位配置，不连接数据库。
迁移流程依据 [EF Core 官方迁移文档](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)。

## 开发与测试

无库演示须为 Identity、Platform、Files、Auditing、Scheduling 分别设置 `<Context>__Storage__Provider=Memory`，仅 `Development` / `Testing` 环境允许。
未指定或空白 Provider 使用 Postgres；拼错值直接失败。三个手动 HTTP 验证脚本已显式选择开发内存模式。
Memory 使用 scoped 工作副本与 singleton 已提交状态；仓储登记及聚合修改在工作单元提交前不向其他请求发布。
普通拒绝与取消丢弃工作副本；错误密码等明确保留的拒绝仍提交。行审计、最小业务事实和业务状态原子保存，
宿主配置 RabbitMQ 后自动交付 Identity Outbox。Memory 中尚未交付的记录随进程结束丢失，不能用于生产持久恢复。
详细语义及代价见 [开发存储提交边界](../src/Services/Identity/docs/adr/0005-memory-storage-has-a-commit-boundary.md)。
Aspire 将 `NEXUSSTACK_DB` 分别注入五个平台模块的 `ConnectionStrings__<Context>`，启动前仍需完成各模块的独立迁移。
AppHost 启动脚本只接受明确的运行库配置，不再把 `NEXUSSTACK_TEST_POSTGRES` 隐式用作应用数据库。

新增真实 HTTP 测试使用 `NEXUSSTACK_TEST_POSTGRES` 所在服务器上的临时数据库（`nsn_identity_journey_*`），
需要测试账号具备建库、删库权限。每条旅程串行运行，结束后删除自己创建的数据库；断线测试只关闭该临时库的连接。
全量仍使用 `scripts/run-tests.ps1`，不并行执行解决方案测试。

## API 资源目录管理

`POST /api/identity/api-resources` 要求显式的同路由 `POST` 管理权限，匿名返回 401，
有效会话但缺少该权限返回 403。根账号经独立迁移和配置播种后可以登记第一个资源，
再通过既有菜单、角色和用户授权链委派资源管理员，无需向全部登录用户开放引导入口。

资源管理权允许改变菜单所推导出的 API 许可，因此是授权目录的管理能力。
用户已有某个菜单的使用权限，并不代表可以往该菜单登记新的管理操作。
授权在平台宿主入口执行，经过网关和直连宿主遵守同一规则；资源提交后的权限缓存失效语义保留。

`IdentityResourceAuthorizationTests` 覆盖已有合法菜单权限的普通用户尝试扩权、显式委派管理员，
以及真实网关、PostgreSQL 独立迁移和进程重启后的拒绝。此修复不改变自注册决定，
也不代表账户生命周期或独立业务宿主的即时撤权已完成。

## 安全状态与当前限制

PostgreSQL 事实容量由 `IdentityFactCapacity` 独立迁移启用；默认 100,000 条、正文总量 256 MiB、单条正文 16 KiB。
整批事实与业务同事务，满额返回 `identity.audit_capacity.exhausted` / HTTP 503，不留下半批变化，
也不执行提交后的权限失效。错误密码只有失败计数及事实成功提交后才返回原有凭据错误。
恢复依靠安全清理已确认副本；容量诊断、Memory 准入与可审计策略调整已交付，见已关闭的票据 #101 与合并 PR #102。
设计与测试边界见 [ADR-0006](../src/Services/Identity/docs/adr/0006-fact-capacity-rejects-the-whole-command.md)。

会话版本由 User 持有；访问令牌和刷新令牌都记录签发时版本，重启后旧版本继续被拒绝。
登出及重放撤销只修改 User 聚合；旧版本重放不会再次撤销后来重新登录的新会话。
权限缓存仍在本进程内，在提交后失效；跨实例权限失效和其他上下文的撤销校验另行设计。
每个 Identity 受保护请求会查询用户的当前安全状态，数据库失败时不会接受旧的缓存结论。
已有令牌流程的跨聚合事务建模债务、固定 WorkerId 以及提交结果不确定时的命令幂等仍未解决。
此轮支持单实例持久化运行，不宣称已经具备多副本高可用。

## 来源事实容量策略

[票据 #101](https://github.com/yongpengW/NexusStackNext/issues/101) 已完成并关闭，
[PR #102](https://github.com/yongpengW/NexusStackNext/pull/102) 已合入 dev `3276cd9`。
Identity 自己的 Memory / PostgreSQL 策略读取、条件调整及有界清理已在该轮交付。
`GET /api/identity/audit-capacity` 增加单调 `policyRevision` 和独立 `controlCapacity`；
`PUT` 同路径要求单独的 PUT 资源权限，仍先验证当前会话，不使用读权限代替写权限。
请求只接受 UUID `requestId`、`expectedPolicyRevision`、三个新额度和固定
`reason=operator-adjustment`，所有 Int64 沿用十进制字符串 HTTP 契约。

策略从版本1开始，实际额度变化才递增；空操作只保留有限凭据。重放先于版本比较，
同请求身份、相同可信操作者和内容返回原裁决，异内容或异操作者冲突。
操作者、所属 schema 与 `identity.fact-capacity-policy-changed.v1` 事件由模块声明，
不接受调用方指定。控制事实不占业务事实额度，默认控制池为1000个请求、16MiB总量和16KiB单条，
每个请求按凭据与实际控制正文的 UTF-8 字节计量；空操作也占一个请求名额。

Memory 可用 `Identity:AuditDelivery:MemoryPolicyControl` 在启动时缩小控制池：
`MaxRecords` 为1–1000，`MaxPayloadBytes` 为1–16MiB，`MaxRecordPayloadBytes` 为1–16KiB且不超过总量。
不配置时沿用默认值；关闭维护也不会绕过启动校验。不支持在线修改，PostgreSQL不使用这组配置覆盖持久额度。

Memory 在原有业务写锁内准备与发布，策略管理不提交用户/角色工作副本，不失效权限缓存，
不推进用户或会话版本。PostgreSQL 使用自己的独立连接与本地事务；
`20261004152243_AuditedFactCapacityPolicy` 是正常增量迁移，不改已有迁移和业务占用。
已接受凭据及对应事实的身份、时间和正文有不可变保护；有控制历史时 Down 明确拒绝破坏性回退。
`FactCapacityPolicyMigrationTests` 已通过私有库的真实升级、重复迁移与治理历史回退拒绝，
经公开策略/仓储端口检查旧额度、占用、角色版本、原授权集合及行审计保持。
该用例重建的是同一测试进程内的宿主实例，不能将它混称为真实进程恢复；该票完整交付资格以原生完成记录为准。

凭据最早保留七天，真实控制事实还需实际交付至少24小时，满足两条条件后才原子删除并释放控制额度。
待投递和死信一直保留；仍被保留的凭据在最早期限之后继续支持重放，只有安全清理后才回到普通版本比较。
公开维护端口支持稳定顺序的一至一千条批次。工作区已接入 `Identity:AuditDelivery:PolicyMaintenance` 调度与
`identity-policy-cleanup` 诊断，参数及期限约束见[共同维护说明](committed-auditing.md)；清理故障不影响业务就绪检查。
中央 typed 摄入、配额/故障边界及六来源协议在 #101 的完整交付范围内，资格见原生完成记录；
后续来源恢复、中央保留归档、导出及遗漏防线继续由 #103 和父 #64 / #60 承载。

## 来源事实条件恢复（开发中）

[六来源恢复 #103](https://github.com/yongpengW/NexusStackNext/issues/103) 仍开放，以下是
`codex/fact-delivery-recovery-103` 的未提交实现，尚未合入 dev，不代表全部六来源验收。
Identity 已成为 Platform 之后的第二个实际消费者，目前提供有界调查、单条状态、条件恢复、凭据读取和容量诊断：
GET `/api/identity/audit-deliveries` 默认查询 Pending、50条；只接受 Pending / Delivered / DeadLettered，
`limit` 为1至100，按发生时刻及消息身份稳定排序。GET `/api/identity/audit-deliveries/{messageId}`
不受列表位置限制。两者要求独立的 GET 资源授权与当前有效会话，状态只含消息身份、状态、失败次数、
下次尝试、停投时刻和十进制字符串恢复代次，不返回消息正文或原始失败。
Memory / PostgreSQL 真实 HTTP 已验证列表之外的单条、三种投递状态、缺失消息404以及容量策略事实。
PG 两条读取都只投影公开状态列；非受管消息在两个来源的列表中排除，单条以所属404拒绝。
读取前后的原消息、业务/策略计量及恢复池保持；非法状态和越界条数经 Memory HTTP 验证为400。

POST `/api/identity/audit-deliveries/{messageId}/retry` 接受稳定 `requestId`、观察到的
`expectedDeadLetteredAt`、`expectedRetryRevision` 及固定 `manual-retry/dependency-restored` 原因；
来源、操作者、接受时刻和执行关联来自宿主，不能通过正文选择。读写要求各自资源权限及当前有效会话。
成功后保持原消息身份、正文和发生时刻，仅开放新投递预算；Int64 仍使用十进制字符串。

GET `/api/identity/audit-deliveries/recoveries/{requestId}` 用于核对原裁决。
GET `/api/identity/audit-deliveries/recovery-capacity` 使用独立读取权限，返回所属来源、持久性和恢复池额度／占用，
不返回原消息、操作者或异常。Memory／PostgreSQL 的真实 HTTP 已验证首次接受计量、同请求重放不重复占用，
业务事实和策略控制池计量保持；四条既有身份恢复、真实进程重启和未提交工作保护旅程也通过。
已有但不属于声明事实的消息以400及 `identity.delivery_recovery.unmanaged` 明确拒绝，
不开放预算或新增凭据；消息归属先于停止时刻精度比较，保留的原凭据重放仍先于当前消息状态。
保留期间同一请求、操作者及内容重放原结果，不再次打开后续停投轮次；原裁决不证明当前投递或中央完成。
恢复使用独立有限池，默认1000个请求、16MiB总量、16KiB单条，最早保留期限固定七天。
Memory 使用原业务写锁且非持久；PostgreSQL 使用自己的独立连接和事务，不自动加入环境事务，
当前访问预算三秒，消息状态、凭据与恢复计数同事务提交。

已验证 Memory 的可信 HTTP 恢复与重放，以及 PostgreSQL 首次接受、实际宿主进程退出/重启后的读取与重放。
两种适配器还已通过公开仓储/恢复端口验证调用者未提交工作：恢复前后新作用域只能读取旧用户/会话版本，
调用者的修改保持在原工作副本，只有原工作单元显式保存才发布；恢复不代为保存或清除业务跟踪状态。
Identity 使用正常增量 `ConditionalFactRecovery` 迁移，包含保留历史的 Down 拒绝；
本轮尚未交付的迁移已增加恢复凭据 UPDATE 不可变保护，请求身份、原裁决 JSON、
接受时计算的字节数和固定七天期限均不可改写。同值 UPDATE 不改变记录，到期删除仍由有限清理执行。
真实数据库已验证四类改写被所属约束拒绝，原裁决可继续读回和重放，计量及原消息保持。
已有停投事实的升级／重复迁移、接受后的回退拒绝及清理后的安全回退／再升级均通过；
两来源凭据保护与既有 Platform 迁移共三项阶段测试通过，六来源完整迁移资格仍待续验。
本轮迁移尚未合并，既有冻结迁移未修改。
两个来源的共同请求／凭据已集中到 BuildingBlocks.Application，固定输入校验、原裁决比较、
七天期限准备及 UTF-8 计量由四个适配器共同使用；所属事件白名单、错误与 HTTP 映射仍在各上下文。
两个来源现在共同使用所属 Memory / PostgreSQL 原子恢复、凭据读取、独立容量诊断和有限清理实现。
共享实现只接收模块声明的所属来源、事件与错误；HTTP 映射、数据模型及增量迁移仍在各上下文。
阶段 Standards 评审指出两套调查实现重复后，共同安全状态 `FactDeliveryState` 放在应用层，
单条读取和有界列表集中到共同存储实现；所属适配器仍提供自己的 Outbox 和事件声明。
PostgreSQL 列表直接投影安全列，Memory 持续使用原业务共用写锁；模块自己的错误码及权限资源保持。
Memory / PostgreSQL 的公开清理端口已验证七天期限、稳定请求顺序、单条批次和计量释放；
PostgreSQL 保留期限向上取整到微秒，不早于原凭据公开的期限。清理不改变原消息、业务或策略控制池。

Memory 可用 `Identity:AuditDelivery:MemoryRecoveryControl` 缩小开发恢复池：
`MaxRecords`、`MaxPayloadBytes`、`MaxRecordPayloadBytes` 均必须为正，最多为默认1000、16MiB、16KiB，
单条上限不能大于总量。真实 HTTP 已验证单条配额下的满额原子拒绝、同请求重放和清理后重新受理。
该测试在模块装配前注入配置；此前应用配置钩子注入过晚的失败是夹具问题，不计为产品红绿证据。
修正夹具后移除模块绑定，构建成功且实际配额断言失败；还原绑定后九项身份恢复相关测试全部通过。

`Identity:AuditDelivery:RecoveryMaintenance` 显式调度所属清理：`Enabled` 默认 true，
`BatchSize` 默认100、允许1至1000；`Interval` 默认一分钟、允许一秒至一小时；
`Timeout` 默认三秒、允许50毫秒至30秒。即使关闭仍验证配置，关闭时不启动清理循环。
`identity-recovery-cleanup` 属于 `auditing-diagnostics`，故障记录安全降级并继续下一轮，
不输出原始异常，不让维护故障改变数据库就绪结论。Memory 真实模块已验证到期后每轮单条释放；
PostgreSQL 真实模块已验证容量释放写失败时凭据与计量完整回滚，解除受控故障后下一轮自动恢复。
该两项与既有配额、恢复及 Platform 维护共十三项测试通过；宿主停止、关闭和更多配置边界仍待续验。
阶段 Standards 评审指出两个来源重复维护协议后，共同清理端口放在应用层，
调度、预算与安全诊断放在共享基础设施。Identity 模块显式选择自己的 `IIdentityAuditDelivery`，
Platform 模块选择自己的 `ISettingAuditDelivery`；两者的配置前缀和诊断名称保持独立。
提取后两种存储的四条真实宿主维护及 Identity 配额旅程共五项通过，不据此宣称整票验收完成。

三套实际网关路由已通过真实 HTTP 验证 Identity 列表匿名401、根账号有效会话列表/单条200。
普通读取权限、恢复拒绝和登出后列表/单条401在直打宿主的真实 HTTP 中验证；
上述历史专项不能混称为普通用户经网关的撤权；六来源新增边缘／授权／安全观察资格见[当前本机状态](handoff-2026-10-03.md)。
调查及授权共十七项阶段用例已通过，不是全票或整仓资格。

更多事务/取消边界、容量/故障/授权及六来源完整迁移资格仍在本票续验或实现，
六来源已阶段接入；最新专项资格及整票剩余项见[当前本机状态](handoff-2026-10-03.md)。继续本机开发，不换机；SignalR 与多机 HA 仍暂缓。
