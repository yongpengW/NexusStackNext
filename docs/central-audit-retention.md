# 中央操作观察保留

中央操作观察默认按最后接收时间保留30天；已提交业务事实及其去重凭据继续保留，尚未提供事实归档。

```json
{
  "Auditing": {
    "Retention": {
      "Enabled": true,
      "ObservationRetention": "30.00:00:00",
      "Interval": "00:05:00",
      "BatchSize": 500
    }
  }
}
```

保留期允许1至3650天，间隔1秒至1天，每批1至1000个操作。宿主启动后自动分批运行，
配置变更需重启；`Enabled=false` 关闭自动清理。缩短保留期会清理符合新期限的既有观察，
请按调查需求设定。PostgreSQL先独立执行 `migrate-auditing` 安装接收时间索引，普通启动不迁移。

按 Source + OperationId 整组判断：所有已有阶段的 RecordedAt 均严格早于截止时刻才到期。
任一阶段刚收到时，旧阶段一起保留；发生在很久以前、刚迟到交付的消息也有完整接收保留期。
来源的 OccurredAt 用于调查发生时间，不用于本次删除资格。

清理在同一提交中删除观察及对应 Inbox/指纹，释放[中央容量](central-audit-capacity.md)。
失败或取消会回滚。正在接纳新阶段的操作留待后续批次，不需要停止消息消费者。
后台异常只记录固定失败信息，下一轮重试；不会生成新的操作观察或删除已提交事实。

观察清理后不再保有这些消息的历史去重对照。原消息再次到达可以重新接纳，RecordedAt重新计时，
OccurredAt保持原值。保留期内相同消息仍幂等，内容或阶段冲突仍拒绝；清理后的接纳不表示业务
再次执行，调查缺失也不表示业务从未发生。此生命周期同样适用于Memory演示适配器。

通过既有 `GET /api/auditing/capacity` 查看清理后的记录数；不提供远程清空或任意截止删除接口。
默认每五分钟最多清理500个操作（至多1000条观察），积压按批次逐步消化。记录删除不代表PG
磁盘立即归还，磁盘维护沿用数据库运维方式。

决定见 [Auditing ADR-0008](../src/Services/Auditing/docs/adr/0008-observation-retention-ends-deduplication.md)。
