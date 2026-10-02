# 已提交事实审计

首条完整链路是全局设置变更：HTTP 授权 → Platform 保存配置与最小事实 → Outbox → RabbitMQ → Auditing 的 Inbox 与不可变记录。配置提交成功后，审计最终可查；回滚、版本冲突和空操作不会生成成功事实。操作观察描述执行尝试，本文的 AuditFact 描述已提交结果，两者不能互相替代。

自动 HTTP 操作记录另见[操作日志](operation-logging.md)：其 Auditing-owned 来源 journal 使用独立连接和事务，业务回滚不会撤销观察；它不替代本链路与业务状态同事务的 Platform Outbox。普通观察允许可见降级，关键事实持久写失败仍必须阻止对应业务提交。

## 配置与启动

Auditing 默认 PostgreSQL，独占 `auditing` schema 与迁移历史。配置 `ConnectionStrings:Auditing`（环境变量 `ConnectionStrings__Auditing`），可以与平台其他模块暂用同一个数据库，也可以指向独立数据库。每个上下文只读写自己的数据。

连接配置只存于被忽略的 `env/platform.dev` 或部署环境。先执行独立迁移，再启动宿主：

```powershell
pwsh -File scripts/migrate-auditing.ps1
# 部署环境直接运行已构建宿主：dotnet NexusStackNext.PlatformHost.dll migrate-auditing
```

迁移命令只读取本上下文连接，既不启动 HTTP、RabbitMQ 或配置中心，也不初始化账号。普通宿主启动不迁移；未迁移、数据库不可用或配置错误都会拒绝启动。仅 Development / Testing 可显式选择 `Auditing:Storage:Provider=Memory`；该模式重启丢失记录。

运行期的中央 Auditing 数据库和审计 broker 检查归 `/health/logging`，故障时报告 `Unhealthy`、HTTP 503；`/health/ready` 排除这些异步日志依赖，只反映处理业务所需的依赖，`/health/live` 仍只检查进程存活。日志探测慢或失败不能让网关摘除业务依赖仍可用的宿主；运维须单独监控日志入口。业务侧保存关键 AuditFact 的 Outbox 仍与业务状态同事务，写入失败照常阻止提交；中央消费不可用与来源事实未能提交是不同故障。完整分组与来源 journal 的降级语义见[操作日志](operation-logging.md#故障策略)。

宿主配置 `RabbitMQ` 后同时启动 Platform 的 Outbox 投递与 Auditing 消费。消费名称默认 `auditing.entries`，可通过 `Auditing:Messaging:ConsumerName` 设置；多副本必须使用同一个名称共享队列。`Auditing:Messaging:Enabled=false` 只用于显式停用消费者。未配置 broker 时，已提交事实保留在 Platform Outbox，待接入 broker 后投递，不能把这时的空查询解释为“从未发生业务变更”。

## 信任与内容边界

本文的事实链路只消费已知契约 `platform.setting-committed.v1`。来源固定为 `platform`，动作由版本契约中的操作映射；客体为设置键和提交版本。Actor 由已认证会话提供，发生时刻由来源服务时钟提供，接收时刻由 Auditing 时钟提供。没有用户身份的受信后台调用 Actor 为空。Trace / correlation 来自来源执行的 Activity，仅用于关联，不能作为认证凭据。

记录不包含配置值、说明、口令、令牌、文件原文或完整请求体。全部环境关闭 `POST /api/auditing/entries`，包括 root 账号。HTTP 客户端无法自己声明 Actor 或 Source 写审计。

消息可信性的边界是 broker 发布权限：服务身份与 RabbitMQ ACL 必须限制谁能向设置事实的路由键发布，并保护传输与凭据。类型名和固定 Source 并不是数字签名；持有发布凭据的进程仍有能力伪造契约。当前平台宿主共用其配置的 RabbitMQ 身份，独立服务部署时要分配受限身份；多机部署加固等待用户决定后再安排。

Auditing 在同一事务内写入 Inbox、内容指纹和记录。相同事件名 / 消息 ID / 内容再次到达，返回重复；同身份不同内容返回 `auditing.message_conflict`，不得覆盖原记录。存储失败抛出并由 broker 保留重投，进程中断不留下只登记去重却丢失事实的半成品。PostgreSQL 时间保存到微秒，指纹基于收到的原始类型化事实，精确重复仍可识别。

## 调查与恢复

在 Identity 中按下列路径和 HTTP 方法登记资源，授予对应角色；仅有调查查询权限不会获得重试权限。已注销或禁用会话不能继续读取，root 同样需要有效会话。

| 接口 | 权限资源 | 行为 |
|---|---|---|
| `GET /api/auditing/entries?page=1&limit=50` | `/api/auditing/entries` + GET | 接收时间、ID 倒序，统一分页响应；page 1–1000，limit 1–100 |
| `GET /api/platform/audit-deliveries?state=DeadLettered&limit=50` | `/api/platform/audit-deliveries` + GET | 状态支持 Pending / Delivered / DeadLettered，limit 1–100，最旧优先；返回消息 ID、次数和投递时间状态，不返回消息正文 |
| `POST /api/platform/audit-deliveries/{messageId}/retry` | `/api/platform/audit-deliveries/{messageId}/retry` + POST | 请求 `expectedDeadLetteredAt` 使用查询得到的时间；仅重试该轮已耗尽投递，状态已变化返回 409 |

查询当前提供最近最多 100000 条记录的分页窗口，尚未提供历史导出或条件检索。总数反映全量记录；持续写入期间不同页不构成一个数据库快照。

Platform 的投递策略由 `Platform:Delivery` 配置，默认最多 8 次失败后保留为 DeadLettered，轮询间隔 2 秒。故障恢复后，受权操作员重新提交同一 ID；成功表示恢复为待投递，并不代表 Auditing 已保存。Delivered 表示 broker 已确认，最终落库以审计查询为准。重复提交或结果未知时先重新查询状态，不能生成新 ID 绕过去重。

业务侧 Outbox 耗尽与消费者死信是两个位置：前者通过上述接口恢复；消费者拒绝非法契约或身份冲突时沿既有 RabbitMQ 重试 / 死信拓扑处理，应排查来源并保留原记录，不能覆盖已接纳事实。数据库故障属于基础设施故障，消费者会重新排队，恢复后继续。

## 验证范围

`AuditBusinessJourneyTests` 使用真实 HTTP、RabbitMQ、临时 PostgreSQL 与真实宿主进程，覆盖提交后生产者重启、回滚 / 空操作 / 冲突、独立审计库断连、重复投递、Inbox 与记录之间崩溃、投递耗尽后的受权恢复。`AuditPersistenceJourneyTests` 覆盖独立迁移、失败事务、并发重复和跨宿主去重；`AuditAccessTests` 验证授权与三份实际网关路由；启动测试覆盖缺失配置和禁止生产 Memory。运行全量使用 `scripts/run-tests.ps1` 串行执行。
