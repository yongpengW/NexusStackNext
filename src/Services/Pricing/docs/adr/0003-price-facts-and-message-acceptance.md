# 定价事实与消息受理分别保存证据

Pricing 既接收人工输入，也接收 Costing 的成本结果。消息确认、任务受理与计算完成是不同阶段，
只观察后台计算会丢失重复、拒绝及提交失败的消息消费过程；直接沿用上游操作又无法表达本地执行。

PriceQuoteCommittedV1 的固定动作是 created、inputs-changed、costing-applied、result-applied。
保存适配器使用共享 CommittedFactInterceptor 生命周期，在本地业务事务中追加最小事实，
与报价、Inbox 及任务共同提交。事实保存失败、取消或租约失权时相关业务状态回滚。
只有 costing-applied 带 Costing 对象引用；同金额的新上游版本仍改变 CostingRevision，必须保留事实。
重复消息、冲突拒绝、旧输入及无变化结果不制造成功变化事实，金额和原始消息不进入审计载荷。

每次通过信封与契约校验的成本消息消费建立 message 操作，固定动作 pricing.cost.accept，
用现有 Subject 字段承载固定事件名和消息 GUID。Started / Finished 保存在独立 journal，
所以业务回滚后仍可见 failed / canceled，重复投递有独立操作标识和 duplicate 结论。
非法信封不伪造业务受理。普通观察仍遵循有界 fail-open，成功事实仍必须与业务原子提交。

首次受理操作成为新任务的直接来源，上游 Costing 执行成为父操作，根操作与原发起人保持不变。
原消息内容及其执行来源仍参与 Inbox 指纹，不能改用本次受理操作计算指纹。
重复投递及重启不改写首次来源；旧任务保留已存来源，不追填新关系。
显式系统执行作用域统一约束行审计和事实 Actor，防止调用链中残留用户被误记为后台执行者。
该标记只决定审计归属，不授予任何权限。

Pricing:Messaging:Enabled 控制成本消费与事实发布，Pricing:Delivery 配置本地 Outbox 交付。
中央通过 Pricing.Contracts 消费最小事实，来源未启用交付时本地事实仍保留。
本轮复用现有报价 Outbox 和观察客体字段，无需新增 Pricing 数据库迁移。

PricingCommittedFactTests、PricingFactCompletionTests、PricingMessageOperationTests 验证来源原子性、
取消、租约失权、身份及重投；TaskOperationJourneyTests 通过真实 HTTP、PostgreSQL、RabbitMQ 和进程重启
验证两次 HTTP、Costing 计算、Pricing 消费、Pricing 计算的五个操作及中央事实关联。
来源容量治理由 #101 / PR #102 完成交付。专门事实恢复在 #103 接入报价变化事实与容量策略事实，
沿用根操作者限制，恢复不重新接纳成本消息、不重算报价，也不登记报价缓存失效意图。
所属恢复状态与不可变凭据原子提交，凭据使用独立有限额度和固定最早保留期限；
本次恢复使用正常增量迁移，有恢复历史时拒绝破坏性回退。中央保留治理及整套六来源恢复验收仍由 #64 / #103 承载。
