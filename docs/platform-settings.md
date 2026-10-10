# 全局设置访问与持久化

HTTP 中的 Int64（ID、version、epoch、size 等）均返回十进制字符串；请求优先原样回传字符串，精确数字输入继续兼容。见 [HTTP Int64 契约](http-int64-contract.md)。

GlobalSetting 是受限的平台元数据。它可能描述邮件、短信或外部应用接入，不能因为是配置就默认公开。
秘密保存在环境或配置中心；设置中的接入信息应使用秘密引用，不能把该模块作为秘密管理器。

所有设置接口都需要有效登录会话和对应操作权限。根管理员用于初始化授权；普通用户通过现有
菜单 → API 资源 → 角色 → 用户链取得权限。读取分组和读取单值是两个独立的授权项。

| 操作 | 注册的路由模板 | 方法 |
|---|---|---|
| 读取单值 | `/api/platform/settings/{key}` | GET |
| 分页读取分组 | `/api/platform/settings` | GET |
| 创建或更新 | `/api/platform/settings/{key}` | PUT |
| 清空值 | `/api/platform/settings/{key}` | DELETE |

权限当前针对操作，不按 scope 或配置键隔离。不要给不应读取整个设置存储的账号授予读取权限。
匿名返回 401，有效登录但无对应权限返回 403；已登出、禁用或缺少会话版本的令牌不能因持有根声明而继续访问。
身份来源故障时请求失败，不使用 Pricing 普通查询缓存或旧会话状态放行。

三份网关路由表均要求 Platform GET 认证；模块内部仍校验会话与权限，直连也必须满足相同要求。
这是原匿名读取契约的有意收紧。需要公开站点名称等信息的项目应提供明确的公开投影，不要重新开放整个设置仓储。
根账号播种和授权操作见 Identity HTTP 接口；`PlatformSettingsAccessTests` 提供完整的授权、读取和登出旅程。

Identity 与 Platform 是共同授权模块的两个真实消费者，HTTP 过滤器因此移到 BuildingBlocks.Web。
会话校验端口由 Identity 实现；Platform 不读取 Identity 表，也不引用它的实现程序集。
普通接口、分页、ProblemDetails 及 OpenAPI 的既有响应保持一致。

权限缓存目前仍限同一个平台宿主的进程内失效，
不能从共享了 HTTP 过滤器推导出多副本即时撤权或独立业务宿主远程授权已经完成。

验证接口：实际平台 HTTP、三份已发布路由的真实 YARP HTTP、独立测试数据库故障后的授权拒绝与恢复。
`scripts/verify-write-paths.ps1` 使用临时内存宿主的测试管理员验证设置写入和读回，不访问共享配置中心。

## 存储与迁移

Platform 默认使用 PostgreSQL，配置 `ConnectionStrings__Platform`（配置中心键为 `ConnectionStrings:Platform`）。
它可与 Identity 的连接指向同一物理库，但拥有独立的 `platform` schema、DbContext、事务和迁移历史。
两个连接配置都须显式提供；Aspire 将同一数据库参数显式注入两处。

首次或升级先执行 `pwsh -File scripts/migrate-platform.ps1`。脚本只从私有 `env/platform.dev`
或进程环境读取 Platform 连接；发布产物使用 `dotnet NexusStackNext.PlatformHost.dll migrate-platform`。
独立命令不启动 Web、配置中心、broker 或账号播种，重复执行幂等。身份迁移仍独立执行
`scripts/migrate-identity.ps1`；正常宿主启动只检查，不自动迁移。

缺连接配置、未应用迁移或数据库不可用会阻止启动。运行期 `/health/ready` 检查 Platform 表能否读取，
`/health/live` 只检查进程。日志中的迁移和启动诊断不输出原始连接异常。
内存演示须显式选择 `Platform__Storage__Provider=Memory`，仅允许 Development / Testing；
无库平台宿主还须显式设置 `Identity__Storage__Provider=Memory`、`Files__Storage__Provider=Memory` 与 `Auditing__Storage__Provider=Memory`。

## 写入与并发

写入成功的 204 表示该配置聚合已提交。稳定键有唯一索引，值、说明和版本在同一次保存中提交；
清空只移除值，保留键和说明。Scope 按第一段精确匹配，`mail` 不会命中 `mail-temp`。

单值和列表返回 `version`，单值同时返回 `description`。未知键返回 `value=null, version=0`；
已注册但清空的键仍有正版本。同值、同说明写入保持版本；值与说明各自变化时各递增一次。

管理端读取后编辑，应在 PUT JSON 中提交 `expectedVersion`，DELETE 则用查询参数 `expectedVersion`。
0 表示只创建尚未注册的键，正数表示只修改该版本；负数返回 400。版本过期或竞争创建返回
409 / `platform.setting.conflict`，调用方重新读取并决定如何处理，不会在后台自动覆盖冲突。

省略客户端版本保留原先的“设置为这个值”语义；它不能识别调用方在请求之前看过的旧数据，
因此不要用它实现用户的读后编辑。即便省略版本，服务器读取到提交之间仍有数据库版本条件；
并发修改不会把不同请求的值与说明拼成一条成功记录。内存适配器也使用快照和版本比较提交，
从前一次读取获得的状态不会随另一个请求的修改而改变。

`GlobalSettingChanged` 目前仍是领域内事实，没有已登记的跨上下文消费者，因此没有对外集成事件映射。
基础表包含 Outbox/Inbox 不代表设置可靠通知或持久审计已完成；真实审计消费者在后续票据接通。
受限元数据不会因为本轮持久化而被广播。

验证涵盖独立临时数据库、授权 HTTP 重启读写、重复迁移、竞争创建和条件更新、保存失败回滚、
独立于 Identity 的数据库掉线/恢复，以及默认持久化和开发内存模式的启动约束。
重叠更新验收用临时库的行锁保证两个请求都读到旧版本；移除数据库版本条件后测试会失败，
恢复后只允许一个请求成功。结果通过 HTTP 检查，锁和活动查询只用于建立重叠条件。

依据：[EF Core 事务](https://learn.microsoft.com/en-us/ef/core/saving/transactions)、
[EF Core 乐观并发](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)。

参考：[ASP.NET Core Minimal API 认证与授权](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/security?view=aspnetcore-10.0)。
框架的 RequireAuthorization 负责要求认证，模块的权限规则仍须显式注册；只要求登录不能代替业务授权。

## 来源事实容量策略

[来源策略治理 #101](https://github.com/yongpengW/NexusStackNext/issues/101) 已完成六来源验收，
经 [PR #102](https://github.com/yongpengW/NexusStackNext/pull/102) 合入 dev。
本节描述 Platform 的接口和维护边界；恢复和中央治理分别按[覆盖矩阵](committed-audit-coverage.md)验收。

GET `/api/platform/audit-capacity` 保留业务容量字段，增加 `policyRevision` 和独立的 `controlCapacity`。
PUT 同一路径接受 `requestId`、`expectedPolicyRevision`、`maxRecords`、`maxPayloadBytes`、
`maxRecordPayloadBytes` 与固定 `reason=operator-adjustment`；长整数沿用十进制字符串契约。
PUT 使用独立资源权限与当前会话，读权限不能用于写。操作者和执行关联来自服务端。

策略版本只在额度改变时推进；空操作保存有限凭据，不发 changed 事实。
同请求、同操作者与同内容重放当时的裁决，重放先于版本比较；旧凭据不会伪称当前策略。
控制凭据与事实共用独立有限池，默认1000个请求、16MiB总载荷、16KiB单请求载荷。
业务额度与控制额度不互相借用；控制池满时拒绝本次调整，已有业务额度仍可使用。
PostgreSQL 的初始控制额度由增量迁移持久保存，宿主启动不覆写。

Memory 可在启动时通过 `Platform:AuditDelivery:MemoryPolicyControl` 缩小控制池：
`MaxRecords` 为1–1000，`MaxPayloadBytes` 为1–16MiB，`MaxRecordPayloadBytes` 为1–16KiB且不超过总量。
不配置时沿用默认值；无效配置即使关闭维护也会拒绝启动。这些值不可在线修改，
不影响业务容量PUT，也不覆盖PostgreSQL的持久额度。

控制凭据最早保留七天，实际 changed 事实还须确认交付并保留确认副本至少24小时，
两个条件满足后才能原子清理凭据和副本、释放控制占用。待投递和死信继续保留；
`retainUntil` 是最早期限，尚未安全清理的凭据继续用于重放与冲突判断。
Memory 使用所属共用写锁，PostgreSQL 使用所属独立连接和短预算事务，清理不改业务额度或策略版本。

`Platform:AuditDelivery:PolicyMaintenance` 控制维护调度：`Enabled` 默认 true，`BatchSize` 默认100
（1–1000），`Interval` 默认一分钟（1秒–1小时），`Timeout` 默认3秒（50毫秒–30秒）。
固定保留期限不能通过这组配置缩短。`platform-policy-cleanup` 诊断使用 `auditing-diagnostics` 标签，
报告维护运行、失败、降级与 `releasedRequests`；空操作凭据的释放也计一次。
失败诊断不输出异常原文，下一轮重新尝试；失败不能发布一半清理结果。

## 来源事实条件恢复

[六来源恢复 #103](https://github.com/yongpengW/NexusStackNext/issues/103)已由[PR107](https://github.com/yongpengW/NexusStackNext/pull/107)合入dev。
以下是当前接口；后续保留与遗漏治理分别见[整体覆盖矩阵](committed-audit-coverage.md)，不由单条恢复旅程代替。

POST `/api/platform/audit-deliveries/{messageId}/retry` 接受稳定 `requestId`、读到的
`expectedDeadLetteredAt`、`expectedRetryRevision` 和固定 `reason=manual-retry/dependency-restored`。
恢复只针对所属业务事实和容量策略事实，保留原消息身份、正文及发生时刻；停止时刻和恢复代次
必须同时匹配。旧的仅凭停止时刻恢复契约已替换，应用存储端口不再提供弱条件重试方法。
有界状态列表同时包含普通设置事实与容量策略事实，裁剪后只提供投递证据，不暴露正文或底层异常。
GET `/api/platform/audit-deliveries/{messageId}` 使用独立单条读取权限，按稳定消息身份调查，
不受列表批次位置限制；只返回最小状态，不存在或不属于可管理事实的消息返回404。
Memory/PG 已验证列表范围外的目标消息及 Pending / DeadLettered / Delivered 状态；
PostgreSQL 单条读取使用所属独立连接与有限预算，不借用调用者事务或读取正文。

成功响应为恢复凭据；相同请求、操作者和内容重放原裁决，不再次开放预算。
异内容或异操作者返回冲突。GET `/api/platform/audit-deliveries/recoveries/{requestId}`
用于响应丢失后读回原裁决；读取凭据与执行恢复有独立权限。
来源、操作者、时刻和执行关联来自可信宿主，不能由客户端正文指定。
Int64 沿用十进制字符串；凭据表示当时开放预算，不表示 broker 或中央已完成处理。

恢复凭据池与业务事实、容量策略控制池分开，当前默认1000个请求、16MiB总量、16KiB单条，
固定最早保留七天。Memory 使用所属共用写锁且不持久；PostgreSQL 使用独立所属连接和事务，
不加入调用者环境事务或清除其工作副本，当前读写总预算为三秒。
条件状态、凭据和计量同事务提交；对结果未知的写入不盲目重试，使用原请求标识核对。

首轮 PostgreSQL 故障资格通过公开存储端口验证：清理已删除凭据、但容量释放写入失败时，
凭据和计量一起回滚，未知数据库约束错误保留原语义；恢复已准备消息状态、但凭据写入等待外部锁时，
调用者取消会结束实际写入并回滚，解除故障后同一请求可重新裁决。
所属恢复账本锁争用返回明确忙碌，不留下恢复凭据或部分消息状态；这些验证不代表全部失败边界已覆盖。

PostgreSQL 使用正常增量迁移，未接受恢复时允许回退；已有恢复历史时拒绝破坏性回退。
本轮尚未交付的 `ConditionalFactRecovery` 迁移还为所属恢复凭据增加 UPDATE 不可变保护：
请求身份、原裁决 JSON、接受时计算的字节数和固定期限均不可改写；同值 UPDATE 不改变记录。
真实数据库已验证四类改写被所属约束拒绝、原凭据仍可读取和重放、到期清理正常释放额度。
空恢复历史的非空事实升级、重复迁移、接受后回退拒绝及安全清理后的回退／再升级均已通过；
原消息身份、内容、发生时刻和当前投递状态保持。本轮迁移尚未合并，既有冻结迁移未修改。
已验证真实进程重启后的原凭据读取与重放，以及微秒精度下过期条件不能被截断成有效条件。
GET `/api/platform/audit-deliveries/recovery-capacity` 使用独立读取权限，返回所属来源、是否持久、
恢复池的上限与当前占用；不返回操作者或原消息正文。重放不重复占用恢复池，恢复不改变另外两池的计量。

到期恢复凭据按最早保留期限、请求标识稳定排序，每轮按有限批次删除并同时释放计量。
清理只删除恢复凭据；原事实、其投递状态、业务额度与策略控制额度不变。
PostgreSQL 保留期限向上取整到微秒，防止早于对外凭据的期限清理。
安全清理后的请求不再承诺旧裁决重放，须按当前停止时刻和恢复代次重新判断。

Memory 可用 `Platform:AuditDelivery:MemoryRecoveryControl` 缩小开发恢复池：
`MaxRecords` 为1–1000，`MaxPayloadBytes` 为1–16MiB，`MaxRecordPayloadBytes` 为1–16KiB且不超过总量。
不配置则沿用默认；这些配置不覆盖 PostgreSQL 持久额度，也不能改变既有七天期限。

`Platform:AuditDelivery:RecoveryMaintenance` 调度所属清理：`Enabled` 默认 true，`BatchSize` 默认100
（1–1000），`Interval` 默认一分钟（1秒–1小时），`Timeout` 默认3秒（50毫秒–30秒）。
无效调度即使关闭维护也拒绝启动。`platform-recovery-cleanup` 使用 `auditing-diagnostics` 标签，
报告运行、失败、降级和 `releasedRequests`；诊断不输出异常原文。宿主关闭会取消本轮等待，
其他维护故障记录后在下一轮尝试，不发布半次清理。
真实 PostgreSQL 宿主已验证清理失败后的完整回滚、独立降级诊断与下一轮恢复：失败时原凭据、
容量计数和原事实保持，诊断不携带原始异常，数据库就绪不因维护降级而误报不可用；
解除受控存储故障后自动清理到期凭据并恢复健康。后续宿主停止与取消边界的最终资格见 #103 完成记录。

Identity 成为第二个已验证消费者后，两个来源共同使用 BuildingBlocks 的条件请求、原裁决凭据、
可信输入校验、重放比较和固定期限／UTF-8 准备规则；HTTP 字段保持不变。
所属 Memory / PostgreSQL 原子恢复、凭据读取、独立容量诊断与有界清理协议已由两个真实消费者共同使用。
两个来源的调查端口也使用共同的安全 `FactDeliveryState` 投影及所属存储协议；来源声明继续决定
可管理事件与未找到的错误。PostgreSQL 列表直接选择六个状态字段，不加载正文或原始失败。
所属事件白名单与 HTTP 语义继续留在来源，迁移模型仍各自拥有；共享 Memory 维护接受时已计算的 UTF-8 大小，
清理只释放原计量，不重新序列化凭据。这些阶段资格不等于整票最终评审或合并资格。
共同维护调度由两个来源的真实宿主行为证明后集中；模块仍显式选择自己的清理端口、
配置前缀及 `platform-recovery-cleanup` 诊断名称。两种存储的维护故障回滚和下一轮恢复继续经真实宿主验证。
已有但不在来源事件白名单中的消息以400及 `platform.delivery_recovery.unmanaged` 拒绝，
不新增凭据或开放预算；其归属先于停止时刻精度比较。Memory 列表也只投影声明的两类事实。

更完整的故障、配置验证和权限边界，以及其他来源、并发和真实消息链路的
完整验收仍在本票范围内。本机继续开发，不换机；SignalR 与多机高可用仍暂缓。
