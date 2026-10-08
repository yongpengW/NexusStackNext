# 用户生命周期与凭据轮换

票据：[Identity 用户生命周期 #53](https://github.com/yongpengW/NexusStackNext/issues/53)。
所有业务请求经网关；身份资料、口令与会话状态仍由 Identity 拥有。

## 查询与条件命令

| 路径 | 授权与输入 | 返回 |
| --- | --- | --- |
| `GET /api/identity/users?afterUserId=0&limit=50` | 同路径 GET 管理权限，limit 1–100，游标非负 | 标识递增页；`nextAfterUserId` 为空表示结束 |
| `GET /api/identity/users/{userId}` | 同模板 GET 管理权限 | 安全用户资料 |
| `GET /api/identity/me` | 当前有效会话，主体取自已验签身份 | 本人安全资料 |
| `POST /api/identity/users/{userId}/disable` | 同模板 POST 管理权限；必填 `expectedVersion` | 204；冲突 409 |
| `POST /api/identity/users/{userId}/enable` | 同模板 POST 管理权限；必填 `expectedVersion` | 204；冲突 409 |
| `POST /api/identity/me/password` | 当前有效会话；必填 `expectedVersion`、`oldPassword`、`newPassword` | 204；旧口令错误 400；冲突 409 |
| `POST /api/identity/users/{userId}/password` | 同模板 POST 管理权限；必填 `expectedVersion`、`newPassword` | 204；冲突 409 |

资料固定包含 `userId`、`userName`、`isEnabled`、有序 `roleIds` 与 `version`。
不返回口令哈希、SessionVersion、刷新秘密或令牌。游标使用稳定标识，避免偏移分页在新增用户时重复或跳过已有用户；
各页读取当前状态，并非冻结全目录快照。JSON 中 long 沿既有 HTTP 契约使用十进制字符串，客户端应原样保存版本与游标。
用户名不能通过这些接口编辑。不存在的目标返回 404；非法标识、缺省或非正版本返回 400。

管理权限通过既有 Menu → ApiResource → Role → User 授予；模板原文和 HTTP 方法须与表中的端点对应。
内置根账号经当前会话权威判定后可以管理。本人端点不接受请求体中的目标用户，不能借字段替换操作对象。
口令重置权限覆盖目标用户，包括内置根账号，属于最高级别的凭据管理权；应只授给可信的恢复管理员。

## 原子变化与失败

禁用实际变化使 `User.Version` 与 `User.SessionVersion` 各 +1；启用只让 Version +1，不能恢复旧会话。
相同启用状态为空操作，两种版本都不变。根账号不能禁用，任何请求都不能修改其内置标志。

口令轮换先验证现有带盐哈希。相同明文是成功的空操作，不重新哈希、不改版本、不撤销会话。
真正的新口令沿注册规则至少 12 个字符；领域只接收哈希。
口令与会话撤销在同一个 User 聚合变化中提交，Version 只 +1。
本人轮换还在事务内检查当前 SessionVersion，避免授权检查与提交之间的撤销竞态。

旧口令错误、未授权、版本冲突与提交失败都不留下半成品。可信来源事实与用户状态一起提交，权限缓存在成功提交后才失效。
失败请求仍可产生独立的操作观察，不表示用户变更成立。普通日志只记录操作元数据，不记录请求正文或凭据。
密码错误的登录仍沿既有策略保存失败次数，连续 5 次锁定 15 分钟；失败的本人轮换不伪造登录失败事实。
当前会话校验会让旧访问令牌在平台、Costing、Pricing 和网关管理面拒绝，旧刷新令牌也失效。
已经受理的后台任务继续用服务身份完成，不因操作者轮换凭据而取消。

## 根账号轮换与恢复

首次启动通过私有 `Identity:Root:UserName` / `Identity:Root:Password` 创建内置用户。
登录后先读取 `/me` 的 Version，再提交本人密码轮换，随后以新口令登录。
重启播种只认已经存在的内置用户，保留当前哈希；修改初始配置口令不能重置已有用户。
普通用户占用了配置中的根用户名时，启动以 `identity.root.name_collision` 拒绝；修正根用户名配置，不能将普通用户提升为根。

在丢失根口令前，准备独立的可信恢复管理员及其凭据保管。
该管理员通过安全用户详情取得根用户的当前 Version，以显式新口令调用管理重置端点，根账号再重新登录。
重置使根的全部旧会话失效。恢复角色至少需要上述详情 GET 与口令重置 POST 权限，不能凭姓名或请求体中的根标志获得它们。
若最后一个具备恢复权限的主体也失去凭据，目前没有离线恢复命令；需另行设计并评审停机维护方案。
不通过改播种配置、默认口令或绕过事务的直接表写入实施恢复。

本轮没有新增数据库列或迁移。物理删除、完整资料编辑、组织与店铺、外部 IdP、MFA 和离线恢复工具尚未实现。
Windows 按影响范围验证；合并仍要求完整隔离 Linux CI 与 Standards / Spec 双轴评审。
