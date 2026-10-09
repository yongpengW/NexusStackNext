# 普通业务操作授权

当前 Costing/Pricing 的真实操作按端点授权；读取不自动授予提交，提交不自动授予任务管理或其他上下文。
机制、并发界限与取舍见[ADR-0028](adr/0028-current-operation-authority.md)，配置沿用[当前会话授权](current-session-authorization.md)。
本机继续开发、不换机，整体 NS/PoS 目标仍暂停；本轮范围是[票据54](https://github.com/yongpengW/NexusStackNext/issues/54)。

## 引导

1. 以当前有效的内置根账号登录，创建代表一组操作的 Menu。
2. 创建 ApiResource：path 使用下表的完整模板，method 使用对应方法，menuId 绑定刚创建的真实菜单。
3. 创建 Role，再通过 `POST /api/identity/roles/{roleId}/menus/{menuId}` 授予菜单。
4. 通过 `POST /api/identity/users/{userId}/roles/{roleId}` 授予角色。普通用户原有效 JWT 即可使用相应操作。

首次资源登记由当前根身份引导，后续目录管理需要独立管理许可；业务读写许可不会授予 API 资源登记权。
API 资源 method 区分 GET/POST/PUT，path 与端点模板对应，不能用实际对象 URL 或带 `:guid`／`:long` 约束的模板。
未绑定菜单的资源不授予普通用户；没有登记或没有有效链路默认 403。匿名 401；Identity 不可用 503，业务尚未开始。

## 条件撤回与恢复

| 管理操作 | 所需操作键 | 请求 |
| --- | --- | --- |
| 读取用户及其版本 | `/api/identity/users/{userId}:GET` | GET 用户详情 |
| 读取角色及其版本 | `/api/identity/roles/{roleId}:GET` | GET 角色详情 |
| 撤回用户角色 | `/api/identity/users/{userId}/roles/{roleId}/revoke:POST` | `{ expectedVersion }`，来自 User |
| 整体替换角色菜单 | `/api/identity/roles/{roleId}/menus:PUT` | `{ expectedVersion, menuIds }`，来自 Role；空数组撤回全部 |

成功 204，过期版本 409；缺版本或非法标识 400，引用不存在 404。menuIds 必须提供且最多 256 项，空数组合法，null 非法。
Int64 输出为字符串；输入接受数字及数字字符串。相同集合或已不存在的角色归属是空操作，版本不变。
角色整体替换不更改 User 版本；用户角色撤回不更改 Session version。重新增量授予后原有效 JWT 可继续使用。
权限管理员须分别授予所需管理操作，不会因为被授某项业务操作就获得管理权。

授权每次从 Identity 主库读取，不依靠诊断权限缓存失效，因此跨实例和热缓存均不能延续已撤回的操作许可。
承诺撤回成功后新开始权威读取的请求拒绝；已通过授权的在途请求可能完成。已受理任务及其历史仍保留，
后台按自身协议完成，不保存 Bearer。查看、重试、取消和交付恢复都需要相应的新操作许可。

## 当前操作表

每一行是一项独立许可，含全部 23 个 Costing 和 14 个 Pricing 真实端点；覆盖测试枚举宿主实际端点及实际策略。
下表由当前代码声明列出，新增接口必须同时声明许可并更新此表。

| 方法 | 模板 |
| --- | --- |
| POST | `/api/costing/batches` |
| GET | `/api/costing/batches` |
| GET | `/api/costing/batches/{batchId}` |
| GET | `/api/costing/batches/{batchId}/rows` |
| GET | `/api/costing/batches/{batchId}/attempts` |
| POST | `/api/costing/batches/{batchId}/retry` |
| POST | `/api/costing/batches/{batchId}/cancel` |
| GET | `/api/costing/audit-capacity` |
| PUT | `/api/costing/audit-capacity` |
| GET | `/api/costing/audit-deliveries` |
| GET | `/api/costing/audit-deliveries/{messageId}` |
| GET | `/api/costing/audit-deliveries/recovery-capacity` |
| POST | `/api/costing/audit-deliveries/{messageId}/retry` |
| GET | `/api/costing/audit-deliveries/recoveries/{requestId}` |
| GET | `/api/costing/schedule-receipts/{occurrenceId}` |
| POST | `/api/costing/cost` |
| GET | `/api/costing/tasks/{taskId}/delivery` |
| POST | `/api/costing/tasks/{taskId}/delivery/retry` |
| GET | `/api/costing/items/{itemId}` |
| GET | `/api/costing/tasks` |
| POST | `/api/costing/tasks/{taskId}/cancel` |
| GET | `/api/costing/tasks/{taskId}` |
| POST | `/api/costing/tasks/{taskId}/retry` |
| GET | `/api/pricing/audit-capacity` |
| PUT | `/api/pricing/audit-capacity` |
| GET | `/api/pricing/audit-deliveries` |
| GET | `/api/pricing/audit-deliveries/{messageId}` |
| GET | `/api/pricing/audit-deliveries/recovery-capacity` |
| POST | `/api/pricing/audit-deliveries/{messageId}/retry` |
| GET | `/api/pricing/audit-deliveries/recoveries/{requestId}` |
| POST | `/api/pricing/cost` |
| POST | `/api/pricing/fee` |
| GET | `/api/pricing/items/{itemId}` |
| GET | `/api/pricing/tasks` |
| POST | `/api/pricing/tasks/{taskId}/cancel` |
| GET | `/api/pricing/tasks/{taskId}` |
| POST | `/api/pricing/tasks/{taskId}/retry` |

## 边界与验证

角色/API 启停、菜单更新／删除、角色物理删除尚无管理 HTTP 生命周期，本票不声称已经实现。
如果目录引用未来消失，当前权威链会忽略它；新授予和绑定校验引用存在。对象级可见范围继续属于业务上下文，
Files 的 Owner 检查独立保留。没有租户权限、权限通配符或并行的权限码体系。

本机按影响范围执行真实 HTTP/ISender 旅程：普通同一 JWT 经真实网关和独立数据库／进程，
提交与撤回、Identity 重启、两个管理员竞争、故障回滚、跨实例热缓存与挂起的权威读取；
被撤回的普通操作者已授权成本工作在重启后仍通过 RabbitMQ 驱动 Pricing。
协议错误、版本／主体／操作不匹配、超时、超大或缺字段响应拒绝且不留下业务输入及任务。
最终 Linux 全量、独立 Standards/Spec 双轴及 dev 交付资格以票据和 PR 原生记录为准。
