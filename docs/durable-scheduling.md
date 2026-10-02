# 持久计划触发与 Costing 重算

Scheduling 保存“何时触发、触发哪个业务操作”，Costing 保存成本输入、接受结论和执行任务。首次支持 `costing.recalculate`，目标为已有 CostSheet 的对象标识；计划不携带金额、URL、类名或脚本。

## 运行与迁移

平台宿主默认使用 `Scheduling:Storage:Provider=Postgres`。配置 `ConnectionStrings:Scheduling`，可以与其他平台模块使用同一物理库，表与迁移历史归 `scheduling` schema。独立迁移命令只读环境变量，不连接配置中心或启动 HTTP：

```powershell
# 私有 env/platform.dev 或部署环境需提供 ConnectionStrings__Scheduling
pwsh -File scripts/migrate-scheduling.ps1
# 发布后的等价入口
dotnet NexusStackNext.PlatformHost.dll migrate-scheduling
```

普通启动检查迁移和数据库，未准备好直接退出；运行期数据库掉线使 `/health/ready` 返回 503，`/health/live` 仍可响应。开发无库演示可以显式选择 `Scheduling__Storage__Provider=Memory`，生产拒绝此模式。原内存计划不会自动导入。

Costing 的新增接受记录与任务来源列需要先执行它自己的 `migrate-costing`，见[成本与定价协作](costing-pricing-cooperation.md)。本轮只在隔离的测试数据库执行迁移。

平台配置 RabbitMQ 后，显式启动 Platform、Scheduling 各自的 Outbox 存储与投递循环，策略分别来自 `Platform:Delivery` 和 `Scheduling:Delivery`。未配置 RabbitMQ 时触发仍落库，交付保持 Pending。

Costing 设置 `Costing:Messaging:Enabled=true` 时同时发布成本结果并消费计划触发；`Costing:Scheduling:Enabled=false` 可关闭该订阅。队列消费组由 `Costing:Scheduling:ConsumerName` 配置，默认 `costing-schedules`。同一应用的多个实例共用消费组，独立测试环境使用独立交换机和消费组。

## 创建与管理计划

通过网关调用 `POST /api/scheduling/tasks`：

```json
{
  "code": "cost.daily-recalculate",
  "intervalSeconds": 86400,
  "firstRunInSeconds": 60,
  "targetKind": "costing.recalculate",
  "targetId": "11111111-1111-1111-1111-111111111111"
}
```

`code` 规范化为小写且唯一。间隔为 1 秒至 366 天，首次延迟为 0 至 366 天；省略首次延迟则从当前时刻开始。目标、操作与委托人在创建时固定；委托人来自已验证会话，HTTP 传入的 actorId 不受信任。

`GET /api/scheduling/tasks?page=1&limit=50` 按稳定标识分页，最大页码 1000，每页最多 200；数据库内直接分页。返回 `version`、目标和下次时刻。暂停与恢复分别调用 `POST /api/scheduling/tasks/{id}/pause`、`/resume`，JSON 为 `{"expectedVersion":1}`；旧版本返回 409，重复暂停保持版本不变。

管理接口要求当前有效会话及独立的 API 权限：

| 方法 | 权限路径 |
|---|---|
| GET / POST | `/api/scheduling/tasks` |
| POST | `/api/scheduling/tasks/{id}/pause` |
| POST | `/api/scheduling/tasks/{id}/resume` |
| GET | `/api/scheduling/tasks/{id}/occurrences` |
| POST | `/api/scheduling/occurrences/{id}/retry` |

列表权限不授予修改、历史调查或重试权限；注销后原会话不能继续管理计划。已登记的发生保留原委托，注销和暂停不会撤回已提交的工作。

## 三段状态分别观察

1. **交付**：`GET /api/scheduling/tasks/{id}/occurrences?page=1&limit=50`，最大页码 1000、每页最多 100。返回原计划时刻、实际触发时刻、发生序号以及 Pending / Delivered / DeadLettered。一次 Occurrence、计划推进和 Outbox 同事务保存；同一计划与序号唯一。
2. **接受**：`GET /api/costing/schedule-receipts/{occurrenceId}`，返回 Accepted 或 Rejected、原委托人、目标及本地任务标识。404 表示尚无本地结论。目标不存在形成稳定拒绝；相同发生标识重投不重新解释，新的意图使用新的发生。
3. **执行与结果交付**：Accepted 后使用返回的 taskId 查询 `/api/costing/tasks/{taskId}`、`/delivery`，以及业务结果 `/api/costing/items/{itemId}`、`/api/pricing/items/{itemId}`。这些业务样板接口沿用各自的根操作者策略。

Delivered 表示 broker 接管，Accepted 表示 Costing 的本地任务已登记；两者都不表示计算成功。相同输入重算可以成功完成而不改变 Costing / Pricing 版本。

计划列表沿用 `lastRunAt` 字段兼容原 HTTP 契约；它表示上次登记触发的时刻。领域方法和事件使用 Triggered 命名，业务完成必须查询 Costing 的任务状态。

自动交付预算耗尽后，读取 `deadLetteredAt`，向 `POST /api/scheduling/occurrences/{occurrenceId}/retry` 提交 `{"expectedDeadLetteredAt":"所读到的 UTC 时刻"}`。状态匹配才返回 202 并恢复交付，重复或过时请求返回 409；发生标识、序号及消息内容保持不变。

## 故障语义与范围

扫描每轮最多 50 个计划，固定节拍为 10 秒。迟到只登记一次，下次时刻从本轮实际触发时间起算；不会把停机期间每个间隔都补跑。单项登记失败保留原状态，后续计划继续处理；日志只记录失败计划标识。

消息采用至少一次交付。Costing 在本地事务中保存 Inbox、内容指纹、接受结论及任务；重复消息只对应一份工作，相同身份换内容会被拒绝。提交前崩溃不留下接受记录或任务，恢复后由原消息重投。手工请求与计划发生不能悄悄复用任务身份。

本轮是固定间隔与一个真实业务目标的完整路径。Cron、时区 / DST、补跑策略、长任务续租和多机部署由后续票据处理。验收入口为 `Scheduling*Tests`、`ScheduledCostingTests` 和 `ScheduledCostBusinessJourneyTests`，仍按仓库脚本串行运行。
