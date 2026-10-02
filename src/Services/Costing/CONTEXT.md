# Costing

独立成本核算参考上下文，拥有成本组成、计算结果和成本事件。

## Language

**CostSheet**:
一个对象的单位采购成本、分摊运费及最近计算出的单位成本。
持久化行保存创建与最近修改的审计信息，查询通过 `audit` 返回。人工输入来自已认证主体；后台结果写入的操作者为空，保留原创建信息。相同输入或相同结果不刷新审计时间。
_Avoid_: PriceQuote、MasterPricing、库存批次

**CostCalculation**:
一次已接受的成本重算工作。输入快照与执行历史持久化，结果过期时不发布成本事件。
受理来源与原发起关系保留不变；当前后台执行者不冒充原发起人。
_Avoid_: ScheduledTask、消息投递、定价重算

**CostRevision**:
成本组成的输入版本。在集成事件里标识结果所依据的输入，不包含任务的执行次数。
_Avoid_: 聚合 Version、ExecutionEpoch

**CostCalculatedV1**:
某个对象、某个成本版本的完整单位成本结果，具有稳定事件标识，不携带下游费率。
_Avoid_: 增量调价命令、采购单、跨库实体

**CostDelivery**:
已计算结果从本地 Outbox 投递到 broker 的状态。Delivered 表示 broker 确认，不表示下游已经完成定价。
_Avoid_: 计算完成、业务全链路完成

**ScheduledCostReceipt**:
Costing 对一个计划发生作出的持久接受或拒绝结论。接受时同时保存本地成本输入快照与任务；重投沿用原结论，不能重新解释原先不存在的目标。
_Avoid_: 消息 ACK、任务执行成功、Scheduling 的交付状态
