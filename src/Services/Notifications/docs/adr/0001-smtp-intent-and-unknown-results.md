# ADR-0001：邮件意图持久化，SMTP 结果未知不自动重发

Status: Accepted

## Context

PoS 的 HTML、多收件人和附件是已使用的能力，用户确认先补 SMTP，短信待供应商确定。
SMTP 在 DATA 后丢失回复不能证明服务器未接纳邮件；数据库事务不能回滚这一外部效果。

## Decision

Notifications 独占不可变邮件与发送生命周期，作为 PlatformHost 可选模块，不新增部署单元。
调用者提供稳定身份，重提内容须一致。模块先提交 Unknown 与尝试次数，再在事务外执行有界 SMTP。
确定接纳/拒绝分别保存 Accepted/Failed；Unknown 保留等待人工条件确认或明确重发，Failed 也需显式重试。
处置需独立权限与本人归属；活动发送期不能重发。原内容及 Message-Id 保持，后者不是幂等保证。
行审计及最小事实随所属事务保存，通过 Contracts 交给 Auditing，不含邮件内容。
生产强制 TLS、正常证书验证，采用 MailKit；不引入供应商通用框架或短信空适配器。

## Consequences

202只表示原意图持久化，Accepted不保证最终收件箱送达。
崩溃可能留下实际上未发送的 Unknown，需要明确裁决，避免将未知误当成安全重试。
敏感 MIME 当前持久保留；业务报表和模板继续属于调用方。
