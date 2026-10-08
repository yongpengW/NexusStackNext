# 当前会话授权运行说明

平台的 Identity、Platform、Files、Scheduling、Auditing 请求过滤器，以及 Costing、Pricing 与网关路由管理面，
在操作执行前读取当前会话状态。JWT 验签通过仍需会话版本有效。机制与并发保证见[ADR-0027](adr/0027-current-session-authority.md)。

## 配置

在网关、Costing、Pricing 的私有环境或配置中心配置固定的 `IdentitySession:BaseAddress`。
地址指向平台宿主本身，不能指回网关或任意回调接口。网关的本机默认是 `http://127.0.0.1:5191/`；
Costing / Pricing 必须显式配置。Aspire 的本机编排向三者注入同一个平台地址。

| 配置 | 默认与范围 |
| --- | --- |
| `IdentitySession__BaseAddress` | 必须为受信 HTTP(S) 根地址；无口令、查询、片段、路径参数 |
| `IdentitySession__Timeout` | 3 秒；50ms–10s；完整请求与响应读取预算 |
| `IdentitySession__MaxConcurrency` | 16；1–128；满时不排队 |
| `IdentitySession__AllowUnencryptedHttp` | false；非回环 HTTP 要显式 true，生产优先 HTTPS |
| `Identity__SessionAuthority__Timeout` | 2 秒；50ms–5s；主库最小读取预算 |
| `Identity__SessionAuthority__MaxConcurrency` | 16；1–128；专用池与读取许可上限 |

三个消费者和 Identity 继续使用匹配的 JWT 签名密钥、Issuer、Audience；值只走私有配置。
会话读取使用 `ConnectionStrings:Identity` 指向的权威主库；本轮没有数据库模型或迁移变化。
允许范围内也应按数据库余量设置并发，不按 HTTP 请求量无限扩展连接。

## 返回与恢复

401 表示令牌无效或会话已不存在、禁用或撤销；403 表示会话有效但缺少当前根操作者权。
503 `identity.session.unavailable` 表示本次无法取得权威结论，受保护操作没有开始。
先检查平台进程、主库、固定地址、证书及调用预算，恢复后新请求重新读取；不需要清空允许缓存，因为没有该缓存。
已经接受的任务继续由后台执行，重新登录后才能查询、提交、重试或取消。

外部身份依赖故障可能让业务 HTTP 暂时拒绝，同时后台工作和宿主存活探针仍可用。
该行为与日志依赖分组不同；本轮没有改变就绪探针的分组或多机故障恢复拓扑。
配置和错误报告只打印键名与分类，不打印 Bearer、连接串或秘密。

HTTP 外部适配器测试使用独立受认证的测试权威服务，只安排业务测试所需的最小主体事实。
撤销与重启旅程使用真实 Identity 与其持久数据库；测试权威替身没有运行时开关，也不会进入产品装配。
