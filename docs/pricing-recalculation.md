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

先经网关登录 Identity 根账号。样板目前只允许有效 JWT 带 `nexusstack:root=true` 的根操作者访问，
普通账号返回 403、未认证返回 401。使用登录返回的 Bearer token，避免把令牌写进共享脚本。
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
状态有 `Pending`、`Running`、`Retry`、`Succeeded`、`Superseded`、`Failed`。
`Superseded` 表示其输入已被更新，该任务不再覆盖当前价格。
`GET /items/{itemId}` 返回输入、聚合版本、已计算输入版本和价格；本例完成后价格为 100。
当 `calculatedRevision < inputRevision` 时，价格尚未对应最新输入，调用方应展示“重算中”。

失败终态允许根操作者 `POST /tasks/{taskId}/retry`，请求体为 `{ "expectedEpoch": "3" }`
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
| `Pricing:Tasks:MaxAttempts` | 3 | 1 至 10 |
| `Pricing:Tasks:RetryDelay` | `00:00:01` | 10 毫秒至 1 小时，乘以本轮尝试次数 |
| `Pricing:Tasks:PollInterval` | `00:00:01` | 10 毫秒至 1 分钟 |

进程崩溃后 Running 任务等待租约到期再接管，超过尝试上限进入 Failed。普通失败进入有界退避或 Failed。
首轮公式是有限本地计算，不提供长任务续期、检查点或任意脚本执行，也没有任务历史清理策略。
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
