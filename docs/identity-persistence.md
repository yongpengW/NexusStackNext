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
恢复依靠安全清理已确认副本；容量诊断和 Memory 准入已完成，可审计策略调整正在票据 #101 的工作分支实施。
设计与测试边界见 [ADR-0006](../src/Services/Identity/docs/adr/0006-fact-capacity-rejects-the-whole-command.md)。

会话版本由 User 持有；访问令牌和刷新令牌都记录签发时版本，重启后旧版本继续被拒绝。
登出及重放撤销只修改 User 聚合；旧版本重放不会再次撤销后来重新登录的新会话。
权限缓存仍在本进程内，在提交后失效；跨实例权限失效和其他上下文的撤销校验另行设计。
每个 Identity 受保护请求会查询用户的当前安全状态，数据库失败时不会接受旧的缓存结论。
已有令牌流程的跨聚合事务建模债务、固定 WorkerId 以及提交结果不确定时的命令幂等仍未解决。
此轮支持单实例持久化运行，不宣称已经具备多副本高可用。

## 来源事实容量策略（实施中）

[票据 #101](https://github.com/yongpengW/NexusStackNext/issues/101) 的当前工作区已接入 Identity 自己的
Memory / PostgreSQL 策略读取、条件调整及有界清理，尚未提交、合并或完成整票验收。
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
该旅程重建的是同一测试进程内的宿主实例；真实进程恢复与本票最终资格仍按 #101 续验，不能混称。

凭据最早保留七天，真实控制事实还需实际交付至少24小时，满足两条条件后才原子删除并释放控制额度。
待投递和死信一直保留；仍被保留的凭据在最早期限之后继续支持重放，只有安全清理后才回到普通版本比较。
公开维护端口支持稳定顺序的一至一千条批次。工作区已接入 `Identity:AuditDelivery:PolicyMaintenance` 调度与
`identity-policy-cleanup` 诊断，参数及期限约束见[共同维护说明](committed-auditing.md)；清理故障不影响业务就绪检查。
中央 typed 摄入、完整配额/故障边界及六来源共同协议仍在实施，最终资格见票据记录。
