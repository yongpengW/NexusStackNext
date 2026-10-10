# ADR-0008：操作观察按最后接收时间整组到期

Status: accepted

## Context

中央操作观察持续占用有限额度，需要正常生命周期。开始和结束分别投递，可能乱序或迟到；
按每行年龄清理会把已有完成证据删掉、留下开始阶段，调查结果倒退为 unconfirmed。
已提交业务事实承担不同的证明职责，不能套用操作观察的删除期限。

## Decision

同一来源、操作的最后 RecordedAt 严格早于截止时刻时，整组观察才可清理。
默认保留30天，可配置；批次默认500个操作。复用 IOperationObservationStore 的清理方法、
现有 Memory 写锁及 PostgreSQL 接纳操作锁。正在接纳的操作本轮跳过；锁定候选后用新语句
快照再次核对期限。观察、所属 Inbox/指纹及容量释放在同一事务提交。

观察清理同时结束对应消息的去重对照。迟到或再次投递的原消息可以重新接纳，按新 RecordedAt
保留，OccurredAt不变；清理后的接纳不能证明该操作第一次发生。业务事实及其去重凭据继续保留。
不增加永久墓碑、水位或清理恢复凭据；归档业务事实需要独立的归档及删除资格方案。

宿主按配置定期执行一批。显式禁用可保留现状；失败不删除部分数据，下一轮重试。
使用现有接纳等待预算和容量查询，不新增清理HTTP接口、专用状态机或健康依赖。

## Consequences

保留期内保留原重复/内容冲突语义。已过期调查证据可能因迟到交付重新出现；不能据缺失或重新
出现推断业务发生次数。缩短配置会使符合新期限的旧观察在后续批次清理，操作者应按调查需求设定。

容量释放是逻辑记录数量变化，不表示数据库磁盘立即缩小。事实归档仍是后续切片。

## References

- [中央操作观察按接收期限整组清理 #158](https://github.com/yongpengW/NexusStackNext/issues/158)
- [PostgreSQL：事务级 advisory locks 与 LIMIT 求值](https://www.postgresql.org/docs/18/explicit-locking.html#ADVISORY-LOCKS)
- [PostgreSQL：数据修改 CTE 与 RETURNING](https://www.postgresql.org/docs/18/queries-with.html#QUERIES-WITH-MODIFYING)
