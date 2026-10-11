# 定价重算样板

HTTP 中的 Int64（ID、version、epoch、size 等）均返回十进制字符串；请求优先原样回传字符串，精确数字输入继续兼容。见 [HTTP Int64 契约](http-int64-contract.md)。

Pricing 是第一个独立业务上下文：自己的宿主、数据库、迁移与任务表。PlatformHost 不装配它。
首轮规格见 [#26](https://github.com/yongpengW/NexusStackNext/issues/26)，实现切片见
[#27](https://github.com/yongpengW/NexusStackNext/issues/27)，所有权决定见 [ADR-0019](adr/0019-context-owned-business-tasks.md)。

演示公式为 `成本 / (1 - 费率)`，结果保留四位小数、中点远离零；不是公司 PoS 的生产定价公式。
成本范围为 0 到 10 亿，费率范围为 0 到 0.99，输入最多四位小数。

## 启动

先创建专用 PostgreSQL 数据库。通过本地凭据文件或部署系统注入下列环境变量，值不要放入命令行、日志或版本库：

| 变量 | 用途 |
|---|---|
| `ConnectionStrings__Pricing` | Pricing 专用库；迁移账号需要 DDL 权限，宿主需要业务读写权限 |
| `Jwt__SigningKey` | 与 Identity/网关相同的签名密钥，至少 32 字节 |
| `Jwt__Issuer` / `Jwt__Audience` | 与签发方一致；默认均为 `nexusstack` |

在已经注入配置的终端执行：

```powershell
dotnet run --project src/Hosts/NexusStackNext.PricingHost -- migrate-pricing
dotnet run --project src/Hosts/NexusStackNext.PricingHost -- --urls http://127.0.0.1:5192
```

迁移命令只读取 `ConnectionStrings__Pricing`，不要求 JWT 或配置中心，成功退出码为 0；可重复执行。
普通启动只检查迁移状态，不建表。缺库、不可达或未迁移时进程退出。
`/health` 与 `/health/live` 仅查进程；`/health/ready` 查数据库、迁移与业务表，运行中断库返回 503。

网关提供可选的 `routes.pricing.json`（默认平台路由加 Pricing），启用方式：

```powershell
$env:Gateway__RouteTablePath = 'routes.pricing.json'
dotnet run --project src/Gateway/NexusStackNext.Gateway -- --urls http://127.0.0.1:5190
```

平台宿主按 [Identity 运行文档](identity-persistence.md) 在 5191 启动。三个进程使用相同 JWT 配置。
启用此路由表后网关就绪检查同时检查平台与 Pricing；默认 `routes.json` 不依赖样板。
两份文件各自可直接部署；自动检查要求样板完整保留默认平台配置，修改平台策略时须同步两份。
Aspire 可选读取 `NEXUSSTACK_PRICING_DB`，非空时增加 Pricing，并切换网关路由表；仍须预先迁移。
这些固定端口用于本地开发。生产只发布网关端口，平台与 Pricing 留在内部网络。

## HTTP 使用

先经网关登录 Identity；根账号可引导配置，普通账号按角色获授各端点的独立操作许可，见[普通业务操作授权](business-operation-authorization.md)。
无许可返回 403、未认证返回 401；根账号也须通过当前会话校验。使用登录返回的 Bearer token，避免把令牌写进共享脚本。
业务路径均从 `http://127.0.0.1:5190/api/pricing` 进入，返回现有统一响应封装。

`POST /cost` 提交如下 JSON：

```json
{
  "requestId": "b2ecc392-f03e-418e-b235-35535be0ac37",
  "itemId": "d092dc07-90d1-451d-8d18-154f76a4637a",
  "expectedVersion": "0",
  "cost": 80,
  "feeRate": 0.2
}
```

返回 202，`data.taskId` 等于 `requestId`，表示成本和任务已经一起提交。
首次创建 `expectedVersion=0`；修改前从 `GET /items/{itemId}` 读取当前 `version`。
版本冲突返回 409；同一请求标识、同一完整内容重试返回原任务，不受之后版本变化影响；
同一标识换内容返回 409。超时不知道是否提交时，保留原请求标识重试或查询。

`GET /tasks/{taskId}` 返回状态、输入版本、执行代次、当前轮尝试次数、等待时间、稳定错误码及历史执行记录。
状态有 `Pending`、`Running`、`Retry`、`Succeeded`、`Superseded`、`Failed`、`Cancelled`。
`Superseded` 表示其输入已被更新，该任务不再覆盖当前价格。
`GET /items/{itemId}` 返回输入、聚合版本、已计算输入版本和价格；本例完成后价格为 100。
当 `calculatedRevision < inputRevision` 时，价格尚未对应最新输入；结合任务状态区分待计算、失败和已取消。
有界任务列表、延迟、条件取消及执行者续租见[业务任务管理](business-task-management.md)。

失败终态允许具有该重试操作许可的用户 `POST /tasks/{taskId}/retry`，请求体为 `{ "expectedEpoch": "3" }`
（值取自查询结果）。成功返回 202；只有仍为 Failed 且代次匹配才接受，否则 409。
重试重置当前轮尝试次数，历史与单调递增的执行代次保留。
接口文档为宿主 `/openapi/v1.json`，网关聚合文档会包含 Pricing。

## 执行与配置

宿主默认运行后台工作进程。`Pricing__Worker__Enabled=false` 可禁用此实例的执行，仍能接受和查询任务。
多个宿主可连接同一个 Pricing 库；领取使用短事务与 `SKIP LOCKED`，计算在领取事务之外。
完成/失败回写校验代次与租约，保存结果时再校验输入版本。结果和终态在一个事务提交。
租约及重试时间使用数据库时钟，避免进程时钟差异决定执行权。

| 配置键（环境变量把冒号换成双下划线） | 默认 | 允许范围 |
|---|---|---|
| `Pricing:Tasks:LeaseDuration` | `00:00:30` | 100 毫秒至 10 分钟 |
| `Pricing:Tasks:MaxLeaseDuration` | `00:30:00` | 不小于 LeaseDuration，最多 24 小时；两个期限均为整微秒 |
| `Pricing:Tasks:MaxAttempts` | 3 | 1 至 10 |
| `Pricing:Tasks:RetryDelay` | `00:00:01` | 10 毫秒至 1 小时，乘以本轮尝试次数 |
| `Pricing:Tasks:PollInterval` | `00:00:01` | 10 毫秒至 1 分钟 |

### 重算完成 Webhook（受控接收端）

启用 `Pricing:Webhook:Enabled=true` 后，重算结果提交会在同一个 Pricing Outbox 事务中追加
`pricing.recalculation-completed.v1`。事件只携带对象 ID、版本、输入版本、结果版本、时间和追踪/关联标识，
不携带成本、费率或结果金额。事件的 `EventId` 同时是 `Idempotency-Key`，重投必须复用它。

| 配置键 | 默认 | 说明 |
|---|---|---|
| `Pricing:Webhook:Enabled` | `false` | 功能开关；默认不产生外部通知 |
| `Pricing:Webhook:Endpoint` | 空 | 启用时必须是 HTTPS 受控接收端 |
| `Pricing:Webhook:MaxAttempts` | `5` | 1 至 10；明确未受理的 429 有界退避，其余永久 4xx 进入失败；结果未知先核对 |

启用 Pricing 的 RabbitMQ 消息配置后，Pricing 先通过本地订阅把事件物化为私有投递记录，再由后台进程在数据库事务之外发送 HTTP；Webhook 开关和 Messaging 开关必须同时启用，否则宿主快速失败。
接收方必须将幂等凭据与外部效果一起持久化，再返回包含原 `EventId`、`IdempotencyKey` 与 `Accepted=true` 的 `PricingWebhookAcceptedV1` JSON 确认。重复 POST 必须复用原结果，不能产生第二次效果。

发送前先条件领取并持久记录 `Unknown` 和本次尝试次数，再在数据库事务之外发送；因此进程在外部完成后、本地保存结果前退出，也不会重新把该通知当成尚未发送。有效确认将它改为 `Succeeded`。超时、断线、408/5xx、无效成功确认以及调用方取消均保留 `Unknown`，worker 不自动重放 POST。取消继续向调用方传播。明确未受理的 429 可以自动重试，合法 `Retry-After` 限制在 0 至 300 秒；缺失或非法值回退到有界指数退避，耗尽进入 `DeadLettered`。其他永久 4xx 进入 `Failed`。这依赖受控接收端约定，不把任意外部供应商的 429 当作未发生效果的证明。

`POST /api/pricing/webhooks/{eventId}/retry` 使用当前尝试次数条件恢复。对于 `Unknown`，先通过 `IPricingWebhookSender.QueryAsync` 在事务之外向原 HTTPS 端点发 GET，携带同一个 `Idempotency-Key` 与追踪标识：

| 核对响应 | 恢复行为 |
|---|---|
| HTTP 200、两个身份匹配、`Accepted=true` | 直接恢复 `Succeeded`，保留尝试次数，不再 POST |
| HTTP 200、两个身份匹配、`Accepted=false` | 接收端明确确认尚未受理；原身份回到立即可领取的 `Pending` |
| 查询不可用、202/404 等其他状态、无效 JSON 或身份不匹配 | 返回 503 / `pricing.webhook.unavailable`，保持原 `Unknown`，不重发 |

接收端的 GET 只核对持久结果，必须与同键的受理写入协调；缺少这个协议的端点不能通过普通 404 被推断为未受理。`Failed`、`DeadLettered` 的人工恢复继续复用原事件和载荷；条件更新拒绝过期的尝试次数或状态。人工开放的通知立即可领取，不依赖数据库与 worker 的墙钟完全一致。

事件携带受理重算的 `ExecutionOrigin`，发送与核对分别通过 `pricing.webhook.deliver` / `pricing.webhook.reconcile` 后台观察记录 OperationId、RootOperationId、TraceId 和 CorrelationId，便于调查关联；确认正文、外部凭据和业务敏感载荷不进入状态或日志。
当前接收端仅用于本地和 CI 验证，不代表已经接入生产供应商。

验证边界为 `ISender`、`IPricingWebhookDeliveryRunner`、受控 HTTP 接收端及既有真实网关旅程。`WebhookProcessHost` 是仅用于测试的可执行装置：两个真实 OS 进程在已落盘而回复未返回时被杀掉，重启后仅查询原键并验证 POST 一次、效果一份；它复用产品 worker 和 HTTP 适配器，仅把测试传输改写到回环接收端，不为测试修改生产 HTTPS 校验或机器证书信任。产品宿主的认证、503 契约和 Costing/Pricing 协作继续由真实网关旅程验收。

进程崩溃后 Running 任务等待租约到期再接管，超过尝试上限进入 Failed。普通失败进入有界退避或 Failed。
公式仍是有限本地计算，现有执行者接口支持固定总预算内的条件续租；检查点、任意脚本执行与历史清理尚未实现。
执行允许重复，数据库的业务结果受条件写入保护；外部邮件、HTTP、支付等副作用不在此保证内。

根身份依赖 JWT 有效期，不读取 Identity 表，也未实现跨上下文即时撤权投影。
生产业务角色、撤权传播、数据库备份及高可用部署仍需后续设计。单机进程恢复不代表主机故障下高可用。
第二消费者与公共任务模块见 #28，Redis 缓存与失效见 #29；首轮不依赖 Redis 或 MQ。

## 验证

```powershell
pwsh -File scripts/run-tests.ps1 -Filter 'FullyQualifiedName~Pricing'
```

测试使用独立临时数据库，并通过 ISender、真实宿主/网关 HTTP、迁移与进程终止验证。
覆盖并发领取、请求去重、旧执行拒写、输入过期、重试历史、登记/完成回滚、重启恢复、认证及健康检查。
数据库账号须可创建和删除临时库；断库测试只终止自己创建的库的连接。
CI 使用独立 PostgreSQL 容器并串行执行这些测试；RabbitMQ 测试仍按原有环境开关决定是否运行。
