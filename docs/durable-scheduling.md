# 持久计划触发与 Costing 重算

HTTP 中的 Int64（ID、version、epoch、size 等）均返回十进制字符串；请求优先原样回传字符串，精确数字输入继续兼容。见 [HTTP Int64 契约](http-int64-contract.md)。

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

2026-10-02 开发阶段确认没有历史数据后，七个上下文的迁移统一重置。`InitialScheduling` 直接创建完整的固定间隔、日历规则、决定、发生、故障退避与计划审计字段，不再支持旧固定间隔迁移链的升级或降级。已有开发 schema 需要清理后重新初始化，不能只清空迁移历史并保留旧表；只处置确认可重建的 NSN schema，不能删除共用数据库中配置中心或其他系统的数据。重复执行当前迁移不会改写已保存的计划、版本或审计信息。启动与就绪检查会读取计划、决定、发生和 Outbox 的所需列，不能只凭迁移历史判断可用。

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

固定间隔统一为微秒精度，采用最近偶数舍入（例如 30.1234567 秒成为 30.123457 秒）。预览、内存和 PostgreSQL 使用同一规范化规则；返回值可以直接回传，相同输入不会因存储精度差异误增规则修订。既有 Interval 数据不重写。

也可以改为显式 `rule`；它与旧的 `intervalSeconds`、`firstRunInSeconds` 互斥：

```json
{
  "code": "cost.business-hours",
  "rule": {
    "kind": "Cron",
    "expression": "10 * 7-23 * * ?",
    "timeZoneId": "Asia/Shanghai",
    "misfirePolicy": "FireOnce",
    "graceSeconds": 30
  },
  "targetKind": "costing.recalculate",
  "targetId": "11111111-1111-1111-1111-111111111111"
}
```

此规则在上海时间 7 至 23 点每分钟的第 10 秒发生。Cron 支持五字段（分钟起）或六字段（秒起），保存实际字段数，表达式最多 256 字符；不支持年份字段、宏或需要随机种子的 H。时区必须使用 IANA 标识，`UTC` 规范化为 `Etc/UTC`，不接受机器当地时区或 Windows 时区名。

每月指定日、短月取月底用 `{"kind":"MonthlyDay","day":31,"hour":9,"minute":0,"timeZoneId":"Asia/Shanghai"}`。标准 Cron 的 31 日会跳过没有 31 日的月份；`L` 才表示月末。固定间隔也可写成 `{"kind":"Interval","intervalSeconds":30}`，创建时立即到期，不附加时区、Cron 或漏跑策略字段。

先向 `POST /api/scheduling/tasks/preview` 提交 `{"rule":{...},"after":"2026-10-02T00:00:00Z","count":10}` 查看规范化规则及 UTC / 当地时刻。after 必须显式带 Z 或偏移；count 为 1 至 10。前瞻窗口五年，稀疏规则返回窗口内可用的项，零项返回 400；预览不会创建计划。创建、更新、恢复和扫描共用这个日历计算。

五年窗口用于公共预览和新规则接受；已接受计划的扫描/恢复则在最多一个 Gregorian 400 年周期内直接求下一次，不枚举节拍。例如 `0 0 1 1 MON` 的 2029-01-01 发生，下一次在 2035-01-01；不能因为下一次超出预览窗口就丢弃当前发生。规则格式不含年份，搜索也不越过可表示日期上限。相同规范化规则的条件更新先识别空操作，仍在数据库裁决版本，当前五年预览为空不会使既有规则的空操作失败。

时刻语义采用 Cronos 0.13.0：春季跳时缺口移到首个有效时刻；秋季固定当地时刻取较早的一次，周期表达式保留回拨两侧的发生。比如纽约 02:30 在春季跳时当天落在 03:00；秋季固定 01:30 只发生一次。不要把周期字段的范围改写成列表以追求文本统一，两者可能有不同 DST 语义。部署需要 OS/ICU/tzdata；升级时区数据前应预览未来时刻差异，已保存的下一 UTC 时刻和历史不会自动改写。

Scheduling 启动和 `scheduling-calendar` readiness 检查固定探测 Etc/UTC、Asia/Shanghai、America/New_York、Australia/Lord_Howe 的解析与日历计算；环境缺失所需 IANA 能力时拒绝启动，健康检查报告不可用。此环境检查适用于 PostgreSQL 与开发 Memory 模式；个别既有计划失效仍按该计划退避处理，不因一个坏计划摘除整个宿主。Windows 不应开启禁用 IANA 转换的 NLS 模式；Unix 镜像应保留时区数据。故障测试在独立进程中使用 Windows NLS / Unix 空 TZDIR，避免污染其他测试进程。[.NET IANA 条件](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.findsystemtimezonebyid)、[.NET Unix 时区目录实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/TimeZoneInfo.Unix.NonAndroid.cs)。

`GET /api/scheduling/tasks?page=1&limit=50` 按稳定标识分页，最大页码 1000，每页最多 200；数据库内直接分页。返回 `version`、目标和下次时刻。暂停与恢复分别调用 `POST /api/scheduling/tasks/{id}/pause`、`/resume`，JSON 为 `{"expectedVersion":"1"}`；旧版本返回 409，重复暂停保持版本不变。

`PUT /api/scheduling/tasks/{id}/rule` 接收 `{"expectedVersion":"2","rule":{...}}`。有效改变递增聚合 version 与 scheduleRevision，并只重排未来；相同规范化规则保持全部状态不变。暂停期间可改规则，但不会启用计划或产生发生；恢复从当前时刻计算下次发生。`scheduleRevision` 与 version 分开：启停、失败退避和扫描改变 version，不改变规则修订。

管理接口要求当前有效会话及独立的 API 权限：

| 方法 | 权限路径 |
|---|---|
| GET / POST | `/api/scheduling/tasks` |
| POST | `/api/scheduling/tasks/preview` |
| PUT | `/api/scheduling/tasks/{id}/rule` |
| POST | `/api/scheduling/tasks/{id}/pause` |
| POST | `/api/scheduling/tasks/{id}/resume` |
| GET | `/api/scheduling/tasks/{id}/occurrences` |
| GET | `/api/scheduling/tasks/{id}/decisions` |
| POST | `/api/scheduling/occurrences/{id}/retry` |

列表权限不授予修改、历史调查或重试权限；注销后原会话不能继续管理计划。已登记的发生保留原委托，注销和暂停不会撤回已提交的工作。

## 三段状态分别观察

调度自身的决定从 `GET /api/scheduling/tasks/{id}/decisions?page=1&limit=50` 查询，最多 1000 页、每页 100。每次决定保留当时规则、修订、原计划时刻、观察时刻、下一时刻及可选发生标识。Triggered 为宽限内触发，Coalesced 为漏跑合并，Skipped 为明确跳过；历史不会被后来的规则更新重新解释。计划推进、决定和可选 Occurrence / Outbox 同事务。

1. **交付**：`GET /api/scheduling/tasks/{id}/occurrences?page=1&limit=50`，最大页码 1000、每页最多 100。返回原计划时刻、实际触发时刻、发生序号以及 Pending / Delivered / DeadLettered。一次 Occurrence、计划推进和 Outbox 同事务保存；同一计划与序号唯一。
2. **接受**：`GET /api/costing/schedule-receipts/{occurrenceId}`，返回 Accepted 或 Rejected、原委托人、目标及本地任务标识。404 表示尚无本地结论。目标不存在形成稳定拒绝；相同发生标识重投不重新解释，新的意图使用新的发生。
3. **执行与结果交付**：Accepted 后使用返回的 taskId 查询 `/api/costing/tasks/{taskId}`、`/delivery`，以及业务结果 `/api/costing/items/{itemId}`、`/api/pricing/items/{itemId}`。这些业务样板接口沿用各自的根操作者策略。

Delivered 表示 broker 接管，Accepted 表示 Costing 的本地任务已登记；两者都不表示计算成功。相同输入重算可以成功完成而不改变 Costing / Pricing 版本。

计划列表沿用 `lastRunAt` 字段兼容原 HTTP 契约；它表示上次登记触发的时刻。领域方法和事件使用 Triggered 命名，业务完成必须查询 Costing 的任务状态。

自动交付预算耗尽后，读取 `deadLetteredAt`，向 `POST /api/scheduling/occurrences/{occurrenceId}/retry` 提交 `{"expectedDeadLetteredAt":"所读到的 UTC 时刻"}`。状态匹配才返回 202 并恢复交付，重复或过时请求返回 409；发生标识、序号及消息内容保持不变。

## 故障语义与范围

扫描每轮最多 50 个计划，节拍为 1 秒；处理耗时和基础设施延迟会影响实际触发时间，这不是硬实时承诺。固定间隔迟到只登记一次，下次时刻从本轮实际触发时间起算。日历迟到在 graceSeconds（默认 30 秒，范围 1 至 3600 秒，包含边界）内正常触发一次；超过宽限时 FireOnce 合并为一次，Skip 只保存决定、不产生业务任务。两者都直接计算严格晚于本轮 now 的下一时刻，不逐个遍历停机期间的节拍。需要逐账期完整补齐的业务须另有领域协议。

计算或登记失败保留原 NextRunAt、LastRunAt 和发生序号，另按版本保存 retryAt、lastSchedulingErrorCode、schedulingFailureCount。退避为 1、2、4、8、16、32 分钟，之后每小时重试；连续坏计划不会一直占满前 50 个位置。成功决定、有效规则变更或恢复清除故障状态；暂停清除 retryAt 并保留诊断，重复同规则更新不清除故障。重启读取持久化退避。数据库完全不可用时连退避也可能保存不了，此时报告失败并继续后续轮次，不报告已触发；错误码与日志不包含异常文本或业务载荷。

消息采用至少一次交付。Costing 在本地事务中保存 Inbox、内容指纹、接受结论及任务；重复消息只对应一份工作，相同身份换内容会被拒绝。提交前崩溃不留下接受记录或任务，恢复后由原消息重投。手工请求与计划发生不能悄悄复用任务身份。

日历规则和故障验收由 [#45](https://github.com/yongpengW/NexusStackNext/issues/45) 跟踪；本文随实现更新，不代替票据中的测试与评审结论。长任务续租和多机部署由后续票据处理。验收入口为 `CalendarScheduling*Tests`、`Scheduling*Tests`、`ScheduledCostingTests` 和 `ScheduledCostBusinessJourneyTests`，仍按仓库脚本串行运行。
