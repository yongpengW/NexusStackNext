# 持久 SMTP 邮件

对应[持久 SMTP 邮件发送、状态查询与结果未知处置](https://github.com/yongpengW/NexusStackNext/issues/200)。
Notifications 是 PlatformHost 显式启用的独立上下文，默认关闭；独占 `notifications` schema、不可变 MIME 和发送生命周期。
支持 PoS 实际使用的 HTML、多收件人及附件；业务报表和模板由调用者生成，不读取其他上下文的表。短信待供应商确定。

## 启用与迁移

私有配置设置 `Notifications:Enabled=true`、`ConnectionStrings:Notifications` 和 `Notifications:Smtp`。
连接可以使用平台数据库，本上下文使用独立 schema 及迁移历史。SMTP 配置：

| 键 | 含义 |
|---|---|
| Host / Port | 固定服务器，端口默认587 |
| Sender | 固定发件邮箱，调用者不能覆盖 |
| Security | `StartTls`（默认）或 `SslOnConnect`，正常验证服务器证书 |
| UserName / Password | 可选认证，必须成对配置，只从私有配置注入 |

不自动降级 TLS，不跳过证书验证；`None` 仅允许 Development / Testing。
独立迁移命令读取环境变量 `ConnectionStrings__Notifications`，不启动 Web、配置中心或消息总线：

```powershell
dotnet run --project src/Hosts/NexusStackNext.PlatformHost -- migrate-notifications
```

普通启动只检查、不迁移；启用但未迁移或数据库不可用时启动失败。运行期数据库故障影响 `/health/ready`。
SMTP 故障记录在邮件状态中，不摘除其他业务仍可用的平台宿主。
`Notifications:Worker:Enabled=false` 暂停新发送，意图与状态继续保留。

## 调用

网关转发 `/api/notifications/*`。所有入口检查当前会话和以下独立操作许可，根账号也只查询/处置本人邮件：

| 入口 | 权限键 |
|---|---|
| POST `/api/notifications/emails` | 同路径 `POST` |
| GET `/api/notifications/emails/{id}` | 该路由模板的 `GET` |
| POST `/api/notifications/emails/{id}/resolve` | 该路由模板的 `POST` |

`SubmitEmail` 包含稳定 `RequestId`、`Recipients`、`Subject`、`TextBody` / `HtmlBody` 和 `Attachments`。
附件项为 `Name` / `ContentType` / `Content`（JSON Base64字节）。至少一种正文；最多10个收件人、5个附件，附件合计2MiB，正文合计262144字符，主题200字符，HTTP入口4MiB。
受理返回统一 `ApiResponse<EmailStatus>`、HTTP202。同一身份与完全相同内容返回原状态，内容或发起者不同返回409，不新建邮件。
202表示意图持久化，状态投影不含主题、地址、正文、附件。

## 结论与处置

先提交 `Pending → Unknown` 及尝试次数，再接触 SMTP；网络操作期间不持有数据库事务。连接/认证/发送总预算20秒。
明确接纳为 `Accepted`；明确拒收或发送前连接失败为 `Failed`。发送阶段断连、超时、取消、进程退出和确认后的数据库故障可能留下 `Unknown`，不会自动重发。
`Accepted` 表示服务器接纳，或操作员根据外部证据确认，不保证最终收件箱送达或已读。

处置请求包含 `ExpectedAttempt` 和 `Decision`：

| 当前状态 | Decision | 结果 |
|---|---|---|
| Failed | `retry` | 原意图回到 Pending |
| Unknown，尝试开始至少1分钟 | `confirm` | 根据外部证据确认 Accepted |
| Unknown，尝试开始至少1分钟 | `resend` | 明确接受重复风险，原意图回到 Pending |

尝试开始1分钟内不接受人工处置；过期代次、非法状态和重复处置返回409。处置回复丢失时先读取当前状态。
重试保留原内容、发件身份和 Message-Id；Message-Id 仅供调查，不是 SMTP 幂等保证。
当前不自动删除敏感 MIME；数据库中内容只供发送，状态API不提供内容读取。

## 审计与验收

登记、尝试、确定结果和人工处置分别与最小 `EmailDeliveryCommittedV1` 在本地 Outbox 同事务提交。
启用 RabbitMQ 后经 Contracts 交给 Auditing；未配置 broker 时事实留在 Outbox，不影响邮件发送。
后台沿用原发起关系，系统执行者为空；事实和观察不采集内容、认证值及 SMTP 自由回复。
`SmtpEmailJourneyTests` / `SmtpEmailHttpJourneyTests` / `SmtpEmailAuditJourneyTests` 验证真实回环协议、权限、取消及进程退出、RabbitMQ交付。
`OperationEndpointInventoryTests` 开启模块验证HTTP入口遗漏清单。本机定向通过不能代替完整 Linux CI 和 Standards / Spec 评审。
测试使用本例独占数据库、回环端口及随机消息拓扑，不向真实收件人发送。

协议依据：[RFC5321](https://www.rfc-editor.org/rfc/rfc5321)、[MailKit SendAsync](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_SendAsync.htm)。
采用 MailKit；[微软文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.mail.smtpclient?view=net-10.0)建议新开发使用替代库。
