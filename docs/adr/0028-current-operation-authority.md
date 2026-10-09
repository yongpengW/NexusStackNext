# 每次请求由 Identity 裁决当前业务操作许可

Costing/Pricing 原先只允许当前根身份。决定沿用 User → Role → Menu → ApiResource 的权限语言，
由 Identity 每次读取已提交的当前会话及单个操作许可，允许普通用户按操作使用独立业务宿主。
对应[票据54](https://github.com/yongpengW/NexusStackNext/issues/54)，接续[ADR-0027](0027-current-session-authority.md)。

## 接口与所有权

端点代码以完整路由模板与 HTTP 方法声明 Permission key，路径参数没有路由约束，查询参数及请求体不能替换该键。
Identity 的 `GET /api/identity/access/v1?permissionKey=...` 从验签 Bearer 取得主体及 Session version；
调用者只能询问自己。这个内部端点不重复进行操作授权，避免递归，不记录来源操作观察。
版本化 `CurrentAccessV1` 的五个必需字段为 contractVersion、subject、sessionVersion、permissionKey、isAllowed。
它只表达本次结论，不能作为后续请求的凭据。

平台模块用 Identity 本机 `IRequestAccessValidator`，独立 Costing/Pricing 宿主通过固定 HTTP 适配器消费
`Identity.Contracts`，没有任何跨上下文表读取。两个业务宿主共用授权模块，并在请求体绑定前授权。
网关路由管理继续要求当前根身份；边缘转发只验签，下游即使被直接调用也检查操作许可。

## 权威读取与故障

PostgreSQL 适配器在主库的一条 SELECT 中读取 User 的启用、Session version、内置身份与有效角色／菜单／API 链。
不存在的引用不产生许可；根旁路同样要求存在、启用及版本匹配。
独立连接的自动提交读取不复用业务 EF 跟踪实体，也不拼接多个快照。
Read Committed 下每条 SELECT 的快照从该查询开始建立，见[PostgreSQL 官方说明](https://www.postgresql.org/docs/16/transaction-iso.html)。
Memory 适配器从同一个已提交状态快照读取，遵循相同规则。

会话与操作读取共用 ADR-0027 的专用池、进程许可、完整时间预算及不排队策略。
两类 HTTP 消费共用有界传输实现；返回必须匹配本次主体、版本、操作键及协议版本。
401 表示身份失效，403 表示有效身份没有操作许可，503 表示权威读取不可用。
无重试、无最终允许缓存，也不回退到 JWT root 提示或 Redis；断线、超时、协议不完整或不匹配均不进入业务处理。
接口仍走统一响应和字符串 Int64 契约。

普通权限集合缓存保留为管理诊断投影；事务提交后仍失效，但它不能承担准入保证。
另一实例的热缓存或迟到旧读取即使仍有旧权限，也不能恢复后续请求的允许。
`/authorize` 管理诊断的单操作结论同样来自权威读取，权限数量是独立的诊断投影，不是原子许可集合证明。
此前缓存授权端口及适配器移除，避免存在两个运行时准入来源。

## 管理与并发

用户角色撤销要求 User 预期 Version；角色菜单集合替换要求 Role 预期 Version。实际变化才推进一次，空操作保持版本；
陈旧版本或数据库乐观冲突返回 409。各自只写一个聚合，不逐个修改受影响用户，也不改变 Session version。
现有增量授角色与授菜单契约保留；分别检查 Role/Menu 存在，API 绑定也检查 Menu 存在。
跨聚合存在性读取不能阻止未来的并发目录删除，因此权威链仍必须忽略悬空引用。
业务许可与权限目录管理许可不同，API 资源登记仍执行 #51 的独立授权约束。

撤销提交成功后新开始的权威授权读取必须拒绝；已经开始或完成授权的在途请求可以完成。
不在业务提交前重复读取 Identity，不承诺分布式瞬时撤回。已经接受的工作及历史不删除，后台不用用户 Bearer，
新查询、取消、重试仍分别检查操作许可。重新授予可恢复同一有效 JWT，无需重新登录。

本票不修改数据库模型或迁移。目录启停、菜单更新／删除、角色物理删除、租户及对象范围继续单独设计。
Files 继续在操作许可之外检查 Owner；多机 HA 最后处理。
