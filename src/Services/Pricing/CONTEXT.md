# Pricing

独立定价参考上下文。用精简规则演示持久化派生计算，不代表公司的正式成本核算模型。

## Language

**PriceQuote**:
一个定价对象的当前成本、费率与最近计算出的演示保本价。对象拥有自己的输入版本和结果版本。
_Avoid_: 正式报价单、MasterPricing、商品主数据

**InputRevision**:
定价输入的版本。只有成本或费率实际改变才递增，计算结果写入不改变它。
_Avoid_: 执行代次、尝试次数

**CalculatedRevision**:
最近结果对应的输入版本。小于当前输入版本时，结果已经过时；零表示尚未计算。
_Avoid_: 最新价格、已完成版本

**Recalculation**:
一次已接受的派生重算工作。重复尝试可以发生，但旧输入的结果不能覆盖当前输入的结果；取消只停止这次计算，不撤销已接受的定价输入。
_Avoid_: ScheduledTask、计划触发、集成事件

**RequestId**:
调用方为同一次成本更新提供并在重试间保留的标识。相同标识不能代表不同输入。
_Avoid_: 领取代次、随机重试号

**ExecutionEpoch**:
一次成功领取所获的执行代次。过期接管和人工重试后重新领取都会获得更大的代次。
_Avoid_: 业务版本、请求标识

**CostingRevision**:
已接纳的上游成本输入版本。零表示手工成本；非零表示成本由 Costing 维护，费率仍由 Pricing 维护。
_Avoid_: 本地 InputRevision、ExecutionEpoch、聚合 Version
