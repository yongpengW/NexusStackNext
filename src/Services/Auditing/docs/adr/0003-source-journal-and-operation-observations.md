---
status: accepted
---

# 独立来源 journal 保存操作观察

操作失败、业务回滚和进程中断也需要可调查的证据，所以操作观察不能放在业务事务里，并随回滚一起消失。本决定在 [ADR-0002](0002-committed-facts-and-controlled-investigation.md) 的已提交事实链路之外增加 OperationObservation；AuditFact 的提交语义和同事务义务保持不变。规格见 GitHub Issues #60，首个实现切片 #61 已完成 PlatformHost 与 PricingHost 样板的实现与核心目标验收；全量回归、CI 及合并门禁以本轮实际结果为准。

SourceJournal 由 **Auditing 拥有**，以独立模块显式组装在每个来源宿主。它使用独立 DbContext、schema、迁移历史、连接和事务，可以暂与来源业务共用物理 PostgreSQL；业务上下文不访问 journal 表、不引用 Auditing 实现。journal 的记录本身就是待投递 Outbox，不再先写一份日志、再另写一份消息。来源先保存稳定消息身份，发布适配器取得 RabbitMQ 确认后标记投递；中央消费者以同一事务保存 Inbox、内容指纹和不可变观察，提交后才确认消费。

开始和完成是两条不可变证据，调查查询按 Operation 聚合。允许 Finished 先到；没有 Finished 时结果为 Unconfirmed，不补造成功、失败或取消，也不把到达先后当成执行先后。HTTP 完成只说明请求管线结束，202 说明受理；业务提交由另行保存的 AuditFact 证明。相同消息身份与内容重投幂等，同身份异内容拒绝，不允许覆盖原记录。

普通观察采集采用 **fail-open，但降级必须可见**：journal 写失败不能篡改业务实际返回结果，必须暴露健康状态、计数和安全诊断，并承认该操作可能没有完整记录。已经保存的 journal 可在 MQ 或中央存储恢复后继续投递；独立事务不意味着物理存储故障已经隔离。关键 AuditFact 继续与业务同事务，事实写入失败阻止相应业务提交。

**业务就绪与异步日志诊断分离**。来源 journal、中央 Auditing 数据库及审计消息检查统一标记 `AuditingDiagnostics.HealthTag`（`auditing-diagnostics`）。PlatformHost / PricingHost 的 `/health/ready` 排除该组，只检查处理业务所需的依赖；独立 `/health/logging` 只运行该组。journal 的运行故障与历史缺失报告 `Degraded` / 200，中央数据库或审计 broker 故障仍明确报告 `Unhealthy` / 503。日志检查不能通过返回失败或等待超时影响网关的业务目的地选择；配置、数据库与迁移的启动检查仍快速失败。

## Considered Options

- **直接写中央 Auditing**：采集变成远端同步依赖，中央故障会拖住每个来源请求；不采用。
- **与业务共用事务或业务 Outbox**：回滚会擦掉失败操作观察，并混淆“看到一次执行”和“业务已提交”；不采用。
- **先写 journal 再另存消息**：引入两次独立写入之间的丢失窗口；journal 本身承担 Outbox。
- **所有观察故障都阻断业务**：把普通可诊断性故障提升为所有业务不可用；仅关键已提交事实维持阻断策略。
- **日志检查保留在业务就绪，只把结果改为 Degraded**：探测耗时仍可能超过网关超时，中央存储和审计 broker 也可能把共宿主业务摘除；采用独立诊断入口。

## Consequences

默认记录固定安全元数据，不采集原始 body、query 值、Authorization、Cookie、密码、令牌或异常原文。身份由来源认证与执行上下文产生，客户端关联值只能用于关联。描述、客体及扩展字段必须经过显式元数据边界，不能反射序列化任意请求对象。

运维必须单独监控 `/health/logging`；业务就绪不能证明日志完整或已经交付。历史缺失在存储恢复后仍可见，共用物理存储故障仍可能同时影响业务与日志。验收须包含独立日志慢故障及中央依赖故障，证明真实网关经过主动探测后仍能处理业务，不能仅以日志入口返回 200 推断流量可用。

宿主组合 Auditing 的来源适配器与业务模块，业务层仅依赖内层端口和必要契约。PlatformHost 与 PricingHost 是 #61 的两个实际消费者；Costing、Gateway 和后台执行关联留到后续切片，不借首个样板宣称全入口覆盖。受权、分页和有界过滤是第一票义务；完整调查维度、容量、积压、清理与保留策略由 #64 完成。新增表采用增量迁移，不重写已合并的初始迁移。SignalR 与多机高可用均不在本决定的实施范围。
