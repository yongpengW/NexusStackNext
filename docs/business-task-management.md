# 业务任务的延迟、查询、取消与续租

Costing 和 Pricing 各自在自己的数据库中持有任务。本轮接口仍要求有效根操作者令牌，经网关进入；
普通用户 403、未认证 401。跨宿主即时撤权和普通角色委派分别由后续 Identity 票据负责。
设计取舍见 [ADR-0022](adr/0022-context-owned-task-management.md)。公式仍为演示规则。

## 接受与延迟

三个手工入口 `POST /api/costing/cost`、`POST /api/pricing/cost`、`POST /api/pricing/fee`
均支持可选整数 `delaySeconds`，范围 0 到 2592000（30 天），省略等价于 0。
成功 202 表示输入和任务一起接受，不表示已经算完。`createdAt` 为首次接受的数据库时刻，
首次 `availableAt = createdAt + delaySeconds`。延迟期间可查询新输入，工作进程不会提前领取。

重试保留完整请求及 `requestId`：相同内容返回原工作和当前结论，不重新计时；更改输入、预期
版本或原始延迟返回 409。取消后的相同请求仍返回 Cancelled；新意图需要新 requestId，并使用
当前业务对象版本。重试、取消和续租均不改 createdAt。

调度事件与成本集成事件仍即时登记可领取工作，不继承手工延迟。时间是本地接受时刻，
不是上游事件发生时间。升级前任务的 createdAt 为 null（未知），不能当成升级当天创建。

## 列表与详情

`GET /api/costing/tasks` 和 `GET /api/pricing/tasks` 支持 `page`（默认 1）、`limit`（默认 50，
1 到 200）、`state`（可选精确状态）和 `itemId`（可选非空 GUID）。允许状态为 Pending、Running、
Retry、Failed、Succeeded、Superseded、Cancelled。偏移 `(page - 1) * limit` 最大 100000，超过
返回 400 和所属上下文的 `invalid_query`，应收窄筛选。

统一分页响应的 `data` 是本页数组；`total`、`totalPage`、`epoch` 等 Int64 均为十进制字符串。
条目含 taskId、itemId、state、createdAt、availableAt、epoch、attempts、leaseUntil、
maxLeaseUntil、errorCode。列表不含输入或 history；单项 `GET /tasks/{taskId}` 保留原有历史详情。
errorCode 是最近失败的稳定错误码；取消一个重试中的任务不会抹去此前失败历史。

按 createdAt 倒序、未知时间最后、taskId 升序。一个请求中的总数和条目来自同一数据库快照；
多个翻页请求之间发生新增或状态改变仍可能造成条目移动，不提供跨请求快照。
leaseUntil 与 maxLeaseUntil 在终态中可能保留最后一次领取的值；只有 Running、同代次且数据库
期限仍有效才表示执行权。取消会清空当前租约，历史仍保留。

## 条件取消

`POST /api/{costing|pricing}/tasks/{taskId}/cancel` 的正文必须显式提供
`{ "expectedEpoch": "0" }`，值取自刚查到的任务；缺字段或非 Int64 输入返回 400。
成功返回 200 和 Cancelled 详情；不存在为 404，状态或代次不匹配为 409 / `cancel_conflict`。

Pending / Retry / Running 均可取消；同 epoch 的重复取消返回原结论。它不会回滚业务输入、
删除历史、撤销旧计算结果或消息。终态已为 Succeeded / Superseded / Failed 时不能伪装成取消。
Failed 仍使用已有 `/retry` 条件接口；一旦进入 Cancelled，`/retry` 不再开放工作。

竞争由任务行锁裁决：取消先提交，旧执行者不能再写结果或 Outbox；完成先提交，取消冲突。
失败先进入 Retry 或人工重试先重新开放工作，随后同 epoch 的取消可以成功，两个操作成功
是合法顺序。取消后的输入仍可能尚未计算，界面须同时查看任务状态，不能一概展示“计算中”。

## 执行者续租

通过所属 `ISender` 发送 `RenewCostingWork(taskId, epoch)` 或 `RenewPricingWork(taskId, epoch)`。
续租未开放 HTTP，也不是 Redis 心跳。成功返回持久化的新 ExpiresAt；代次、尝试次数与历史条数
均不增加。仅 Running、原租约有效、同 epoch 且能严格延长时成功；失效、取消、被接管、总预算
耗尽或旧记录无总预算时返回 `renew_conflict`。未找到返回 `not_found`。

| 两宿主的 `Costing:Tasks` / `Pricing:Tasks` 配置 | 默认 | 范围 |
|---|---|---|
| `LeaseDuration` | 30 秒 | 100 毫秒至 10 分钟，整微秒 |
| `MaxLeaseDuration` | 30 分钟 | 不小于 LeaseDuration，最大 24 小时，整微秒 |

每次 Claim 固定 maxLeaseUntil；后续放宽配置或重启不能把它往后移动。续租保存后复检原期限，
若写入被阻塞至原期限失效则回滚。执行者以续租返回值和后续条件提交结论判断执行权；租约丢失
后停止当前工作，不试图通过旧 epoch 复活。当前演示计算很短，后台循环无需自动心跳；长任务
执行者在事务之外做工作，再按实际进度使用该接口，检查点和批次协议属于后续切片。

升级时先停止旧工作进程，再分别执行 `migrate-costing` / `migrate-pricing`；普通启动不迁移。
旧 Running 的 maxLeaseUntil 为 null，原期限内完成/失败继续可用，到期接管会生成新预算。
COMMIT 响应丢失时用新请求范围查询原任务，不凭连接异常断言回滚；续租没有“某次心跳恰好一次”
回执。结果条件写入也不覆盖外部 HTTP、邮件、支付副作用或多机基础设施可用性。
