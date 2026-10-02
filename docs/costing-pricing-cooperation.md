# 成本 → 定价参考链路

[本轮票据](https://github.com/yongpengW/NexusStackNext/issues/28)。演示公式为 Costing 的采购成本加单位运费，
以及 Pricing 的单位成本除以一减费率。这些公式不代表 PoS 生产规则。

## 配置和启动

准备两个独立 PostgreSQL 数据库，分别配置 `ConnectionStrings__Costing`、`ConnectionStrings__Pricing`。
配置放在被忽略的 `env/*.dev` 或部署环境中，凭据不进入命令参数。两个宿主和网关使用相同的
`Jwt__SigningKey`、`Jwt__Issuer`、`Jwt__Audience`；Issuer / Audience 默认采用 `nexusstack`。

先显式迁移；普通启动只检查，不自动建表：

```powershell
dotnet run --project src/Hosts/NexusStackNext.CostingHost -- migrate-costing
dotnet run --project src/Hosts/NexusStackNext.PricingHost -- migrate-pricing
```

两边设置相同的 `RabbitMq__HostName`、`RabbitMq__Port`、`RabbitMq__UserName`、`RabbitMq__Password`、
`RabbitMq__VirtualHost`、`RabbitMq__ExchangeName`。显式开启 `Costing__Messaging__Enabled=true`、
`Pricing__Messaging__Enabled=true`；默认关闭，可独立演示本地计算。

Costing 开启消息后也会订阅 Scheduling 的计划触发；配置、迁移、接受结论和固定间隔语义见[持久调度](durable-scheduling.md)。
消费组 `Costing__Scheduling__ConsumerName` 默认 `costing-schedules`；独立测试环境应同时隔离交换机和消费组。

```powershell
dotnet run --project src/Hosts/NexusStackNext.PricingHost -- --urls http://127.0.0.1:5192
dotnet run --project src/Hosts/NexusStackNext.CostingHost -- --urls http://127.0.0.1:5193
# 网关环境中设置 Gateway__RouteTablePath=routes.business.json
dotnet run --project src/Gateway/NexusStackNext.Gateway -- --urls http://127.0.0.1:5190
```

Pricing 声明自己的持久队列、绑定与死信队列，建议先启动。生产者先启动时，不可路由事件在 Outbox 中重试，
预算耗尽后可人工重投。消费端名 `Pricing__Messaging__ConsumerName` 默认 `pricing-cost`，重启时保持稳定；
改名会创建另一份订阅。不同环境使用不同 virtual host 或消费端名称；只改 exchange 不能隔离队列。

网关默认路由仍只有平台；`routes.pricing.json` 用于单独定价，`routes.business.json` 加入成本并完整保留既有配置。
生产编排只发布网关端口。这里的 loopback 端口用于本机测试，多机高可用最后另开一轮。

Aspire 本地编排可同时设置 `NEXUSSTACK_PRICING_DB`、`NEXUSSTACK_COSTING_DB`，并提供上述完整 `RabbitMq__*` 配置；
它会显式启用两边消息、选择 business 路由。仅配置 Pricing 数据库时仍是上一轮的独立定价模式。

## HTTP 使用

所有接口均需有效根操作者令牌，使用统一返回格式。

1. `POST /api/costing/cost`：`requestId`、`itemId`、`expectedVersion`（创建为 0）、`purchaseCost`、`freightCost`。
   返回 202 和持久任务。重试保留 requestId，相同标识不同内容返回 409。
2. `GET /api/costing/tasks/{id}` 查计算及历史，`GET /api/costing/items/{id}` 查成本及版本。
3. `GET /api/costing/tasks/{id}/delivery` 查 Pending / Delivered / DeadLettered；尚无计算结果时返回 404。
4. `GET /api/pricing/items/{id}` 查成本、费率、`costingRevision` 和结果；比较 `inputRevision` / `calculatedRevision` 判断新鲜度。
5. `POST /api/pricing/fee`：`requestId`、`itemId`、`expectedVersion`、`feeRate`，只修改费率并重算。

首次成本事件创建对象时费率为零。已有人工定价对象接纳第一条成本事件后，由 Costing 接管成本、保留本地费率；
此后 `/api/pricing/cost` 覆盖不同成本返回 `pricing.cost_owned_by_costing` / 409。正式业务须补充渠道与定价政策。

## 故障恢复

| 故障 | 行为 | 恢复 |
|---|---|---|
| 计算进程终止 | 持久租约过期后可接管 | 重启所属宿主 |
| 成本或 Outbox 写入失败 | 成本结果、任务终态、事件一起回滚 | 有限自动重试或人工重试 |
| broker 不可用或无绑定 | 成本可计算；投递重试，预算耗尽进入 DeadLettered | 修复后人工重投 |
| Pricing 数据库暂时不可用 | 原消息不被确认，退避重投 | 修复数据库 |
| 消费事务中进程被杀 | Inbox、业务与任务一起回滚 | 重启后 broker 重投 |
| 提交成功后 ACK 丢失 | 重投被已提交 Inbox 吸收 | 无需人工操作 |
| 旧版本晚到 | 不覆盖新成本，不登记多余计算 | 无需人工操作 |
| 同一消息 ID 内容改变 | 载荷指纹不符，明确拒绝并隔离 | 修复生产者，使用正确的事件身份与内容 |
| 非法事件 | 确认搬入消费端 `.dead` 队列 | 检查契约后经 broker 管理工具重投，不删除 Inbox |

计算失败：`POST /api/costing/tasks/{id}/retry`，正文含查询到的 `expectedEpoch`。
投递失败：`POST /api/costing/tasks/{id}/delivery/retry`，正文含查询到的 `expectedDeadLetteredAt`，消息 ID 保留。
计算完成、broker 确认、下游完成是三个状态；Delivered 只表示 broker 确认。

`/health/live` 只查进程；`/health/ready` 查数据库与迁移，启用消息时还查 broker 认证及交换机。
它不证明下游完成或死信为空，还需观察投递查询和 broker 指标。当前每个服务配置一个投递循环；
多实例投递协调、保留清理和完整运维面板留后续运营能力。

## 验证

测试使用临时独立数据库、真实 RabbitMQ 与宿主进程。结果经 HTTP / ISender / 消息端口断言，
SQL 只用于故障注入和确认屏障命中。覆盖网关入口、Outbox 提交后重启、消费事务中杀进程、重复/乱序、
租约及结果回滚。CI 同样提供专属 PostgreSQL、RabbitMQ，串行测试；这些不证明多节点容灾或生产容量。
