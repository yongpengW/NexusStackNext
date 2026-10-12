using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Notifications.Application;

/// <summary>邮件附件，由调用者提供固定字节，不读取跨上下文文件。</summary>
public sealed record EmailAttachment(string Name, string ContentType, byte[] Content);
/// <summary>以稳定身份登记不可变邮件；正文与地址不会返回到状态投影。</summary>
[BackgroundWorkAcceptance]
public sealed record SubmitEmail(Guid RequestId, string[] Recipients, string Subject, string? TextBody, string? HtmlBody,
    EmailAttachment[] Attachments) : ICommand<EmailStatus>;
/// <summary>读取本人邮件状态。</summary>
public sealed record GetEmail(Guid Id) : IQuery<EmailStatus>;
/// <summary>按观察到的尝试代次明确处置邮件。</summary>
public sealed record ResolveEmail(Guid Id, int ExpectedAttempt, string Decision) : ICommand<EmailStatus>;
/// <summary>不包含收件地址、正文或附件的状态。</summary>
public sealed record EmailStatus(Guid Id, string State, int Attempt, long Version, DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt, DateTimeOffset? AcceptedAt, DateTimeOffset? ResolveAfter, string? ErrorCode);
/// <summary>宿主内部协调命令，一轮最多发送一个待处理意图；不映射为 HTTP。</summary>
[CommandObservationSuppression("内部轮询不是独立业务操作；实际SMTP尝试由后台执行观察记录。")]
public sealed record DispatchPendingEmail : ICommand<int>;
