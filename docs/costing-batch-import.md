# Costing JSON 批次导入

[交付票据：Costing 批次导入：行结论与可恢复检查点](https://github.com/yongpengW/NexusStackNext/issues/50)。
这是一条成本导入竖切，演示公式仍为采购成本加单位运费。不是公司 PoS 成本公式、Excel 适配器或通用批次框架。
业务角色授权、文件适配和私有异步导出由后续票据交付；当前全部接口沿用根操作者策略。

## 接受不可变输入

经网关调用 `POST /api/costing/batches`：

```json
{
  "batchRequestId": "a7255cfb-c088-46a0-a764-914e061672d4",
  "rows": [
    {
      "sourceRow": 12,
      "itemId": "6503487a-f474-4b3f-ad90-276c2d427ca9",
      "expectedVersion": "0",
      "purchaseCost": 80,
      "freightCost": 20
    }
  ]
}
```

202 的 `data` 是批次元数据，表示输入和待执行记录已持久保存，不表示已导入或定价完成。
`sourceRow` 可省略，默认原始数组位置，从 1 开始；`expectedVersion` 必填，零表示对象尚不存在。
Int64 版本、代次、分页总数返回十进制字符串，调用方原样回传，见 [Int64 契约](http-int64-contract.md)。

每批 1–5000 行，实际 UTF-8 请求最多 2 MiB，包括 chunked；先有界读取再解析。
每个金额非负、不超过十亿、最多四位小数，合计不超过十亿。未知字段、重复 JSON 属性、错误类型明确拒绝。
全部原始行先验证，再按 ItemId 末条生效。无效行即使会被重复策略淘汰，也使整批拒绝。
400 返回固定 `errorCode`、完整 `errorCount` 和前 20 条 `errors`（`sourceRow/field/code`），不回显金额或未知字段名。
413 表示字节超限，415 表示媒体类型不支持；拒绝不改成本对象，不留下可执行半批次。

`batchRequestId` 由调用方生成并持久保留。`v1` 摘要包含所有原始行、原始位置、顺序及末条策略，
数值等价和 JSON 属性顺序不造成冲突；输入或行序改变返回 409。
相同身份并发重放返回首次接受的记录；已取消、已失败的批次也不因重放重新执行。
子任务身份按版本化算法从批次和生效行 Sequence 派生，接受时即保留，与手工/计划请求互斥。
没有自动清理这些身份或输入快照。

## 分开观察三个阶段

| 接口 | 返回内容 |
|---|---|
| `GET /api/costing/batches?page=1&limit=50&state=Pending` | 批次列表与总数，state 可省略 |
| `GET /api/costing/batches/{id}` | 计数、检查点、状态、Epoch、尝试数、租约、稳定技术错误码 |
| `GET /api/costing/batches/{id}/rows?page=1&limit=50` | 原始位置、行结论、生效行关联、保留 TaskId、当前子计算状态 |
| `GET /api/costing/batches/{id}/attempts?page=1&limit=50` | 有界执行历史，按 Epoch 升序 |
| `GET /api/costing/tasks/{taskId}` | 已登记的子计算，batch 来源含批次、Sequence、SourceRow |
| `GET /api/costing/tasks/{taskId}/delivery` | 成本结果的 broker 交付状态 |
| `GET /api/pricing/items/{itemId}` | Pricing 自己的本地计算结果 |

分页默认 50、最大 200，偏移最多 100000；列表不加载快照或全部历史。批次状态包括 Pending、Running、
Retry、Failed、Cancelled、Completed、CompletedWithErrors。每个原始行都有一个可解释结论：

- DuplicateSuperseded：更早的重复行，不登记计算；EffectiveSequence 指向末条。
- Imported / Unchanged：输入已受理且登记独立子计算。Unchanged 不增加成本对象版本或刷新审计时间。
- Rejected：ExpectedVersion 冲突，保存 `costing.version_conflict`，不修改成本或登记计算。
- Pending：尚未提交的生效行，TaskId 只是保留身份，任务查询可能 404。

全部计数之和等于 TotalRows。Checkpoint 表示按原始 Sequence 连续提交到的位置，包含重复行。
重复行接受时已经有结论，所以它们的计数可先于 Checkpoint；不是执行乱序。
有业务拒绝时最终为 CompletedWithErrors，包括全部拒绝；修正业务冲突需要新批次身份。
Completed 只表示导入行已处理。子计算、Outbox 交付和 Pricing 结果分别查询，任何阶段都不冒充全链完成。

## 中断、取消与恢复

`POST /api/costing/batches/{id}/cancel`，正文 `{"expectedEpoch":"1"}`。同代次重复取消幂等；
父锁裁决竞争：取消先赢，不留下尾行；行先赢，返回进度包含该行；末行完成先赢，取消 409。
已导入对象、子任务和事件保留。子计算继续，如需停止它，使用其自己的条件取消接口。
未执行行保持 Pending，取消批次不会自动重开。

`POST /api/costing/batches/{id}/retry` 使用同样的条件正文，只接受 Failed 当前代次，202 后保留输入、
检查点和行身份，重新开放有限尝试预算。Cancelled 和已完成批次不能这样重试。
技术故障不伪装业务 Rejected。保存失败回滚该行全部状态，之前行保留；重启或租约接管从已提交位置继续。
COMMIT 应答丢失时，调用方可能收到异常，而事务已经提交：先查询，再用原身份重放，不生成新身份。

## 配置、迁移与诊断

先运行 Costing 的显式 `migrate-costing` 命令，见 [成本 → 定价](costing-pricing-cooperation.md)。
普通启动不迁移，缺批次表、列或迁移时快速失败。旧单项任务保留，不补造批次来源。
新表仅在 Costing 的 schema 中，是技术执行元数据；业务成本审计仍由 CostSheet 及所属提交事实承担。

| 配置 | 默认 / 边界 |
|---|---|
| `Costing:Worker:Enabled` | 开启批次导入与既有子计算后台进程 |
| `Costing:Batches:SegmentSize` | 100，1–500，每段读取上限，逐行独立提交 |
| `Costing:Batches:RowTimeout` | 10 秒，2–30 秒；包含领取、续租及管理短事务 |
| `Costing:Batches:AcceptanceTimeout` | 30 秒，5–60 秒，包含连接、身份锁及快照写入 |
| `Costing:Tasks:LeaseDuration / MaxLeaseDuration / MaxAttempts / RetryDelay / PollInterval` | 沿用所属任务的有界执行策略 |

锁等待 1 秒；受理 SQL 每条 5 秒，执行及管理 SQL 每条 1.5 秒，全部事务另受总预算约束。
受理父身份使用不持有业务锁的有界轮询，使较大批次的并发重放可等首次写入完成再核对原记录。
每个循环只执行一个批次，父批次 → 行/请求 → Item 的锁顺序固定，行提交前重新检查数据库时间与原租约。
逐行自动续租不移动本次领取的 MaxLeaseUntil。达到总期限就停止，下一次有效领取从检查点继续。
改变段大小不改身份、排序或恢复点。以上上限是首版约束，不是吞吐量或完成时间 SLO。

Costing `/health/ready` 检查本地持久受理所需数据库与迁移；MQ 离线不阻断输入受理、查询和本地成本计算。
`/health/delivery` 独立检查启用后的 broker 认证和交换机，故障为 503；关闭消息时没有检查项，不代表交付已验证。
`/health/logging` 仍独立诊断日志。诊断成功也不代表积压已清空，应同时观察任务交付与 Pricing 结果。
MQ 长期故障仍可能耗尽所属事实容量，容量拒绝及投递重试继续按原协议处理，不能忽略 503 或死信。

后台记录 `costing.batch.segment` 的每段执行证据，保存原受理操作关联。段观察 completed 不表示整批完成；
以批次持久状态和子任务结果裁决。对象变化事实同各行事务提交，不把空操作或回滚行记成已提交变化。

测试面为 HTTP / ISender 和真实网关、PostgreSQL、进程及 RabbitMQ。
专项包括并发规范化重放、超限与错误定位、三种写失败回滚、非空检查点、租约失效、取消末行竞争，
以及真实 PostgreSQL COMMIT 完成应答被截断后核对与重放。故障注入只作用于测试临时库，不操作共享服务配置。
完整 Linux CI 与双轴评审的最终证据以原生票据和 PR 为准。
