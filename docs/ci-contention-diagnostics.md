# 恢复账本争用与定价断连旅程的失败诊断

[竞争类偶发失败 #109](https://github.com/yongpengW/NexusStackNext/issues/109) 跟踪两种不同现象。
整体 NS/PoS 能力目标仍暂停；继续本机开发、使用共用测试服务，不启动本机数据库或扩大负载并发。
最终双轴评审、完整 Linux 四报告与合并状态以票据原生记录为准。

## 恢复账本的两个截止时间

旧 PostgreSQL 恢复适配器从连接准备前开始三秒总预算，同时在事务内配置三秒 `lock_timeout`。
锁等待在服务器真正尝试获取锁时才计时；连接、事务与配置的时间已经消耗了总预算。
因此总预算可能先取消，恢复返回 `audit_capacity.unavailable`，清理抛出取消异常，尚未收到服务器锁拒绝。
这不是错误分类遗漏：没有收到明确锁拒绝时，不能把一般连接故障或总预算耗尽伪称为争用。
[PostgreSQL 参数定义](https://www.postgresql.org/docs/current/runtime-config-client.html#GUC-LOCK-TIMEOUT)
区分每次锁获取的等待上限与整个操作的截止时间。

现在恢复/清理独立事务的数据库锁等待上限为一秒，原三秒总预算、调用者取消与事务边界保留。
预留时间给连接准备、锁拒绝响应和回滚；极慢连接或网络仍可能耗尽总预算，并不承诺所有超时均为 busy。
仍只在获取 `fact_recovery_control` 账本时把真实 `55P03` 转译成 busy；其他异常保留原语义。
不改业务事实容量触发器、Pricing 的 250ms 争用夹具或迁移历史，不引入整个恢复命令重试。

回归通过 `ISettingAuditDelivery` 与所属 Outbox 端口观察；只在本例独立库持有账本行锁。
透明本机 TCP 代理为下一份服务器响应注入 600ms 延迟，不解析载荷，不记录 SQL 或凭据。
`DelayedDatabaseReply_LedgerContentionReturnsBusyWithoutChangingTheStoppedFact_AndAllowsOriginalRetry`
在旧实现下明确返回 unavailable；修复后返回 busy。
`DelayedDatabaseReply_RecoveryCleanupReturnsBusyWithoutChangingTheStoppedFact_AndAllowsOriginalRetry`
在旧实现下抛出取消；修复后抛出明确争用异常。
两例都验证事实、容量与恢复凭据没有半成品，释放锁后原恢复请求可接受。
代理使用随机本机端口，关闭时取消并等待全部转发任务；VerifyFull 连接不能用于改写 DNS 的透明故障夹具，不降级产品 TLS。

## 定价是另一种恢复问题

原 Linux 失败位于断连恢复后的 POST 应返回 202 的断言；历史日志没有保存实际状态码。
不能说原日志已证明热缓存值不同，也不能把本轮账本超时归因套到这条旅程。
[数据库恢复 #105](https://github.com/yongpengW/NexusStackNext/issues/105) 与
[PR108](https://github.com/yongpengW/NexusStackNext/pull/108) 已处理受控复现的失效池连接：
只在 BEGIN/请求锁准备阶段重新准备一次，开始业务写入后不自动重放命令。
本轮保留并复验原 Redis/进程重启/数据库故障旅程与成本、费率恢复和写中断线回归，不再修改 Pricing 产品行为。

## 可公开的失败证据

用例通过 xUnit 输出 `NSN_CONTENTION`；只有失败用例的私有 TRX StdOut 经白名单后进入运行器摘要：

- recovery：`busy`、`unavailable`、`succeeded`，只接受 True/False。
- cleanup：`busy`、`canceled`，只接受 True/False。
- pricing：固定阶段 initial/cost_refusal/fee_refusal/missing_task/recovered，与 100–599 三位状态码。

整个输入行必须匹配固定格式；尾随文本、未知阶段或越界状态拒绝公开。
通过用例的输出不作为失败摘要打印；异常原文、任意错误码、ID、正文、连接串与理论参数继续私有保存。
`scripts/check-test-progress.ps1` 通过真实外部 CLI 的英/中文成功与失败路径验证证据传播、泄漏拒绝、退出码和所有权。
不能通过放宽断言或重跑到绿关闭本票。
