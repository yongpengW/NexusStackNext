# 业务事实容量由所属数据库原子裁决

Platform 和 Identity 已分别证明需要相同的容量规则。将账本映射与 PostgreSQL 触发器协议提取到 BuildingBlocks，Files 作为第三个消费者显式接入；每个上下文仍独占自己的 `fact_capacity` 和 Outbox，没有共享连接或跨上下文读取。事实名称、迁移、业务错误翻译与 HTTP 语义仍由上下文决定。

数据库保存策略和计数，宿主启动不覆盖它们。插入事实时按条数、UTF-8 总载荷和单条载荷同时准入，删除时释放同一份额度；批次失败与额度变化随业务事务一起回滚。确认交付本身不释放额度，只在保留期后的已确认副本清理成功时释放。普通业务消息不占事实额度；事实身份和载荷不可改写，避免通过更新逃逸计数。代价是同一来源的事实写入需要串行更新一行账本，写入锁等待由 V2 协议限定。

既有迁移保持冻结，通过新迁移接入共用实现。`CommittedFactCapacityMigrationV1` 是迁移历史的一部分，后续改变协议须创建新版本与向前迁移，不能改动 V1 令全新数据库与已部署数据库产生不同规则。既有 Platform / Identity 只替换等价触发器并更新模型快照，不重建表、不重置额度；Files 从既有事实回填占用，不清理未交付记录。

文件字节与元数据不是分布式事务。上传容量拒绝不发布元数据，未引用的字节沿用已有受保护孤儿回收协议；不能仅因 HTTP 失败就直接删除字节。删除申请准入失败时仍可下载；已经受理的删除若在确认字节清除时遇到容量拒绝，保留可恢复待办，不报告已完成，也不撤销已提交的删除申请。

六个模块在自身 HTTP 边界提供受权只读容量快照，端口位于 Application，PostgreSQL / Memory 适配器位于 Infrastructure。
PostgreSQL 只读取所属账本的一行，连接与查询共用短预算，不使用 EF 的业务重试或当前 DbContext 事务；同一作用域内尚未提交的事实不提前计入诊断。
读取使用独立 Npgsql 连接，取消采用 `CancellationTimeout=-1`，避免预算耗尽后再等待服务器取消响应；含义见 [Npgsql 连接参数](https://www.npgsql.org/doc/connection-string-parameters#timeouts-and-keepalive)。
没有用超时包装丢弃仍运行的 Task，读取及连接释放都被等待；故障测试在持锁时验证请求终止和服务器活动查询结束。
Memory 读取与准入、清理共用原锁，争用时立即报告不可读。两种适配器都不扫描历史正文，读取失败不返回虚构的零值。
细节和读取预算范围见[提交后审计的容量诊断](../committed-auditing.md#来源事实容量只读诊断)；策略调整仍待后续治理。

## V2：容量锁等待有界，拒绝仍保持原子性

[#93](https://github.com/yongpengW/NexusStackNext/issues/93) 用各来源正常向前迁移替换触发器函数，保留账本、策略与既有占用，Down 恢复 V1。历史 V1 与历史迁移不改写。
所属模块将经过校验的 `<Context>:AuditDelivery:CapacityWrite:Timeout` 通过连接 Options 中的私有 `nsn.fact_capacity_wait_ms` 参数传入；未配置时三秒，支持 50ms–30s 整毫秒。直接存储消费者未设置私有参数时也由触发器使用三秒默认值。

仅事实 INSERT / DELETE 更新容量账本时局部设置 `lock_timeout`，完成后恢复原设置；较小的正数原设置优先。普通消息和事实交付状态更新不进入这段逻辑，业务表锁等待、连接与身份权威预算不因此改变。
这是一条锁获取上限，PostgreSQL 对每次锁获取分别计时；它不是 HTTP 或整个事务截止时间。批次在本事务取得账本锁后复用该锁。[PostgreSQL 锁等待参数](https://www.postgresql.org/docs/current/runtime-config-client.html#GUC-LOCK-TIMEOUT)

嵌套 PL/pgSQL 异常块捕获容量操作的 `lock_not_available`，以 `P0001` 和所属 `<schema>_fact_capacity_busy` 约束重新抛出；其他异常和取消保持原语义。
不能把原始 `55P03` 直接交给业务执行策略，因为 [Npgsql 10.0.3](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/PostgresException.cs) 将它列为 transient，[EF 适配器](https://github.com/npgsql/efcore.pg/blob/v10.0.3/src/EFCore.PG/Storage/Internal/NpgsqlTransientExceptionDetector.cs) 会据此重试。
所属适配器只翻译精确的 SQLSTATE 与约束，返回 `audit_capacity.busy`，HTTP 为 503；不把争用伪称为额度耗尽，也不把任意存储故障翻译成该错误。
拒绝时整个业务/事实/任务批次回滚，提交后权限或价格缓存失效不能先行。清理的单条 DELETE 同样在容量拒绝时全部回滚，已受理文件删除继续保留可恢复待办。

本轮只处理 PostgreSQL 容量锁；Memory 提交锁的争用预算、容量策略调整审计、恢复与保留治理仍属 #64 / #60。
