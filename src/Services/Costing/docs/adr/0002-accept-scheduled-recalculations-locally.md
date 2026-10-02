# 在 Costing 内接受计划重算

Scheduling 的 `ScheduleTriggeredV1` 只携带发生身份、计划序号、时刻与原委托人，首个目标操作为 `costing.recalculate`。Costing 在自己的数据库事务里完成 Inbox 去重、内容指纹、接受结论与持久任务登记，沿用发生标识作为任务标识；确认消费发生在事务提交之后。

接受时按成本对象锁读取当前输入，输入快照留在 Costing 内。计划不能携带金额或替 Costing 改输入。目标不存在时保存 `Rejected` 结论并完成消费；以后补建目标也不能把原发生改成接受，新的业务意图必须使用新的发生身份。相同身份的不同内容不通过去重，人工请求也不能复用已经归计划发生所有的标识。

原委托人表示创建计划时作出的后台授权，不是供每次消费重新验证的登录会话。注销撤销管理会话，暂停阻止未来发生，已登记发生继续交付。成本样板的 HTTP 查询仍受本上下文的根操作者策略保护。

`Accepted` 只表示本地任务已登记。调用方先查接受结论，再按任务标识查执行与成本结果交付；相同输入的成功重算可以不改变成本或定价版本。本决定不包含 Cron、时区政策或多机部署承诺。

对应 [Scheduling 的发生登记决定](../../../Scheduling/docs/adr/0002-durable-occurrences-and-business-delegation.md)，由[持久计划触发到 Costing 任务的完整链路](https://github.com/yongpengW/NexusStackNext/issues/43)验收。
