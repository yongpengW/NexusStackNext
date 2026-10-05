# 成本对象事实跟随本地状态提交

成本业务事件包含金额，承担下游 Pricing 的输入同步；自动操作日志还需要最小、不可变的本地状态变化证据。
两者的接收方、内容与管理语义不同，不能用 CostCalculatedV1 同时充当审计记录。

Costing.Contracts 增加 CostSheetCommittedV1，固定动作为 created、inputs-changed、result-applied。
来源保存适配器比较本上下文待提交状态与原始状态，事实携带最终聚合版本，在同一次 SaveChanges 中写入本地 Outbox。
创建输入时与任务受理同事务，应用结果时与任务终态及 CostCalculatedV1 同事务。
重复请求、相同输入、过期输入和相同计算结果均不增加对象变化事实。相同结果的有效重算仍可发布已有业务契约，
所以业务事件数、任务数和已提交事实数不要求相等。

来源事实写失败阻止业务提交；执行取消或保存过程中租约到期时，结果与所有新消息回滚。
过期租约之后由新一轮接管，不能把未提交尝试写成 result-applied。未知执行关联保持未知；
有观察作用域时记录当前操作及原始发起关系，后台 Actor 为空，不冒充最初提交输入的用户。

Identity 与 Costing 已证明保存生命周期机制相同，因而在 BuildingBlocks.Infrastructure 提取
CommittedFactInterceptor<TContext>：只负责完整批次暂存和保存失败/取消时清理，具体动作映射与字段仍由各上下文定义。
整个批次先完成序列化，再附加到跟踪器，构造中断不会留下半批次；共享机制不扫描其他上下文。

中央 Auditing 只消费已知版本契约，使用固定 source / action / subjectType，验证标识与执行关联，
在自己的事务中登记去重身份和不可变事实。来源重启后继续从本地 Outbox 交付，中央暂不可用不撤销来源已提交状态。
CostDelivery 的查询和重试仍仅管理 CostCalculatedV1。来源容量治理由 #101 / PR #102 完成交付；
专门事实恢复在 #103 接入成本对象事实与容量策略事实，沿用根操作者限制，不能借用 CostDelivery 或计算任务重试。
所属恢复状态与不可变凭据原子提交，凭据使用独立有限额度和固定最早保留期限；
使用正常增量迁移，有恢复历史时拒绝破坏性回退。恢复与到期维护不改变成本输入、结果、任务历史或两种事实额度。
中央保留治理及整套六来源恢复验收仍由 #64 / #103 继续承载。

验收边界是 ISender / IOutboxStore、真实 PostgreSQL、HTTP、生产者重启及真实 RabbitMQ，
对应 CostingCommittedFactTests、CostingFactCompletionTests、CostingCommittedAuditTests 与 CostingFactIngestionTests。
