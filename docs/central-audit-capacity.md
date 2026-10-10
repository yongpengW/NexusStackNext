# 中央审计接纳容量

[中央审计有限接纳与容量诊断](https://github.com/yongpengW/NexusStackNext/issues/156) 是父票
[业务事实审计覆盖与容量治理](https://github.com/yongpengW/NexusStackNext/issues/64) 的一个切片。
它限制中央调查存储的记录数量；来源 Outbox/journal 的容量、恢复和清理继续使用原接口。

## 配置与诊断

平台宿主显式装配 Auditing 时读取以下配置，Memory 与 PostgreSQL 同形：

```json
{
  "Auditing": {
    "Capacity": {
      "MaxFacts": 1000000,
      "MaxObservations": 2000000,
      "WaitTimeoutMilliseconds": 3000
    }
  }
}
```

记录额度各为 1..1000000000；等待为 50..30000 毫秒。非法配置在装配时失败。
配置调整需重启中央宿主；同一库的副本应一致配置。滚动调整期间诊断的 `instanceLimit`
是应答实例的上限，允许写入的总体上界是参与写入实例的最大上限，不能当作带版本的全局策略。
默认值是模板起点，投产需结合实际保留量和磁盘余量设定；记录额度不是磁盘字节配额。

`GET /api/auditing/capacity` 需独立资源权限 `/api/auditing/capacity:GET`（根账号可读）。
拥有事实/观察调查权限不会自动获得容量权限。统一响应中的 `facts` 和 `observations` 各包含
`records`、`instanceLimit`、`available`；长整数沿用统一 HTTP 精度契约。
调查最近七天的默认窗口不影响记录计量，开始/结束观察分别占额度。降低上限保留所有记录，
超额时 `available` 为零；诊断失败返回 503，不伪报零占用。Memory 两池分别原子读取，
不宣称跨池同一时刻；PostgreSQL 使用同条查询的快照。

查询带操作日志抑制元数据，防止读诊断产生更多观察。容量满在 `/health/logging` 降级，
存储故障在同一诊断中不可用；`/health/ready` 不包含这些日志依赖。

## 接纳与恢复

额度满时相同消息仍幂等，身份或阶段冲突仍按原协议拒绝。新消息抛出安全的暂时存储故障；
RabbitMQ 消费者延迟后将原消息重新入队，不确认或转移该消息，也不增加无效消息重试次数。
来源的发布确认只表示 broker 接收，不能替代中央调查证明。

优先检查容量诊断与磁盘监控，补充容量后统一调整中央实例配置并正常重启。
broker 中的原消息可再次接纳，无需改变其身份、内容、发生时间或源凭据。
不能用清空 Inbox、指纹或事实的方式恢复容量。

PostgreSQL 必须先独立执行 `migrate-auditing`。新增迁移
`20261010010500_CentralAuditCapacity` 在同一事务中锁定两张记录表、回填数量并安装计量触发器；
有限锁等待失败时迁移回滚，可在消除竞争后重试，不修改旧迁移。
技术账本没有业务操作者审计字段；它不是业务实体，而是记录数量的派生计量。
接纳的 Inbox、内容指纹、记录和额度同事务成功或回滚，普通写入不扫描历史记录。
Memory 是进程内演示实现，重启会清空；PostgreSQL 计量随记录持久保留。

## 剩余工作

操作观察的定期整组清理与去重期限见[中央操作观察保留](central-audit-retention.md)。用户于2026-10-10确认
中央事实及对应Inbox/指纹长期保留，归档删除暂缓；记录年龄、来源副本清理或普通导出都不授予删除资格。
有限接纳额度继续生效，运维按实际保留量规划容量。决定见
[Auditing ADR-0009](../src/Services/Auditing/docs/adr/0009-facts-remain-online-with-deduplication.md)。
新增入口遗漏检查由[审计义务清单](https://github.com/yongpengW/NexusStackNext/issues/160)验收，登记不代替业务行为证明。

决定见 [Auditing ADR-0007](../src/Services/Auditing/docs/adr/0007-central-admission-preserves-evidence.md)。
