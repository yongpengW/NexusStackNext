# 每次请求由 Identity 裁决当前会话

Costing、Pricing 和网关管理面过去只检查访问令牌的签名与 root 声明，平台登出并不能撤销这些入口。
决定以 Identity 的当前用户状态和持久 Session version 为权威，每次受保护请求都取得新判定；
接受同步依赖的可用性代价，换取登出成功后新开始的授权拒绝旧令牌。对应[跨宿主会话撤销](https://github.com/yongpengW/NexusStackNext/issues/52)。

## 接口与所有权

Identity 的 `GET /api/identity/session/v1` 只从本次已验签 Bearer 取得主体与版本；没有可选择其他用户的请求体。
成功返回四个必要字段：`contractVersion=1`、`subject`、`sessionVersion`、`isRoot`。
不存在、禁用、缺失或不匹配的版本返回 401；根身份来自当前 User 的内置状态，不从令牌 root 提示或请求参数取结论。

平台模块消费本机 `ISessionValidator` 适配器，独立宿主通过固定 HTTP 适配器消费 `Identity.Contracts`。
两个适配器都返回有效会话与当前根身份，或者认证拒绝／来源不可用。跨上下文没有表访问、导航或 Identity 内部程序集依赖。
共享的根会话授权模块被 Costing、Pricing 与网关管理面三个消费者实际使用；边缘转发仍只做验签，最终授权由所属模块执行。
普通业务角色、角色即时撤权、OIDC/MFA、密钥轮换和多机 HA 保持后续票据的范围。

## 故障与预算

有效普通身份没有根操作者权返回 403。权威服务不可达、超时、额度满、重定向、正文传输中断、非 JSON、超大响应、错误协议版本、缺字段、主体或版本不匹配返回稳定 503 `identity.session.unavailable`，操作尚未执行。
不重试、不跨请求缓存允许，不回退到 JWT root 声明。Redis 不参与会话版本或根身份的权威读取；普通权限缓存的故障仍沿原有拒绝语义处理。

Identity 从自己配置的数据库主库读取最小的版本／启用／内置字段，不使用业务 EF 的跟踪实体或执行重试。
独立连接池与进程许可默认最多 16 路，总预算默认 2 秒（允许 50ms–5s），满时立即拒绝。
专用连接设置 `CancellationTimeout=-1`，避免预算耗尽后继续等待服务器取消确认；该参数的含义见[Npgsql 官方文档](https://www.npgsql.org/doc/connection-string-parameters.html#timeouts-and-keepalive)。
已有业务 EF 命令的重试与事务配置保留。配置不得指向有复制延迟的只读副本，否则无法满足撤销后的读取保证。

HTTP 调用总预算默认 3 秒（允许 50ms–10s），最多 16 路；响应上限 4KiB，包含无 Content-Length 的流式响应。
固定来源必须为 HTTP(S) 根地址，不含用户信息、查询、片段或回调路径；非回环明文 HTTP 必须显式接受。
TLS 使用系统证书校验，没有跳过校验的入口。客户端关闭 Cookie 与[自动重定向](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.allowautoredirect?view=net-10.0)。
Bearer 只出现在当前受认证请求的授权头，发送到这一个固定来源；不进入 URL、全局客户端头、任务、消息或授权缓存。
内部会话读取排除请求操作观察，避免每次授权放大来源日志；业务入口的授权结果仍按来源操作记录，HTTP 客户端显式屏蔽授权头。

## 撤销的并发边界

登出提交成功后新开始的权威授权读取必须拒绝旧版本。已经通过授权的在途请求可以完成，
不在保存业务状态前额外读取 Identity，也不承诺分布式瞬时撤回。
业务测试用所属 Costing 表锁证明请求已进入业务读取，再完成登出并释放锁：原请求可提交，新查询／提交／重试／取消拒绝旧令牌。

已经接受的 CostCalculation 由所属后台执行，使用保存的输入与发起关系。提交者登出不撤销该工作；
Costing 重启后仍可计算，通过消息驱动 Pricing，查询完成状态须重新登录。任务不会保存用户 Bearer。
数据库故障与恢复、真实进程停止与重启、刷新重放、无效令牌、协议故障和重定向均在 HTTP 测试面验收；
路由覆盖检查枚举真实端点与实际授权策略，且通过删除一处要求的可编译变异验证它确实会失败。
