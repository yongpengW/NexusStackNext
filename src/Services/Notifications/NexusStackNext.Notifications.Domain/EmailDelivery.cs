using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Notifications.Domain;

/// <summary>由调用方提供的邮件意图身份。</summary>
public readonly record struct EmailDeliveryId(Guid Value);

/// <summary>邮件发送的持久生命周期；正文与地址不进入操作状态。</summary>
public sealed class EmailDelivery : AuditedAggregateRoot<EmailDeliveryId>
{
    private EmailDelivery(EmailDeliveryId id, string ownerId, string digest) : base(id)
    {
        OwnerId = ownerId;
        Digest = digest;
    }

    /// <summary>原发起者。</summary>
    public string OwnerId { get; private set; }
    /// <summary>不可变内容的摘要。</summary>
    public string Digest { get; private set; }
    /// <summary>Pending、Unknown、Accepted 或 Failed。</summary>
    public string State { get; private set; } = "Pending";
    /// <summary>已开始的发送次数。</summary>
    public int Attempt { get; private set; }
    /// <summary>Unknown 可人工处置的最早时刻，为当前尝试保留确认等待窗口。</summary>
    public DateTimeOffset? ResolveAfter { get; private set; }
    /// <summary>接收确认时刻。</summary>
    public DateTimeOffset? AcceptedAt { get; private set; }
    /// <summary>固定错误码，不保存服务器自由文本。</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>登记尚未发送的意图。</summary>
    public static EmailDelivery Create(EmailDeliveryId id, string ownerId, string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        return new(id, ownerId, digest);
    }

    /// <summary>开始一次尝试；先提交此状态再接触 SMTP。</summary>
    public bool TryStart(DateTimeOffset now)
    {
        if (State != "Pending") { return false; }
        State = "Unknown";
        Attempt++;
        ResolveAfter = now.AddMinutes(1);
        ErrorCode = "awaiting_confirmation";
        BumpVersion();
        return true;
    }

    /// <summary>保存当前尝试的确定结果；迟到结果为空操作。</summary>
    public bool Complete(int attempt, bool accepted, string? errorCode, DateTimeOffset now)
    {
        if (State != "Unknown" || Attempt != attempt) { return false; }
        var state = accepted ? "Accepted" : errorCode is null ? "Unknown" : "Failed";
        if (State == state) { return false; }
        State = state;
        AcceptedAt = accepted ? now : null;
        ResolveAfter = null;
        ErrorCode = accepted ? null : errorCode;
        BumpVersion();
        return true;
    }

    /// <summary>条件处置：retry 仅用于明确失败；resend/confirm 仅用于结果未知。</summary>
    public bool Resolve(int attempt, string decision, DateTimeOffset now)
    {
        if (Attempt != attempt) { return false; }
        if (State == "Failed" && decision == "retry") { State = "Pending"; }
        else if (State == "Unknown" && ResolveAfter <= now && decision is "resend" or "confirm")
        {
            State = decision == "confirm" ? "Accepted" : "Pending";
            AcceptedAt = decision == "confirm" ? now : null;
        }
        else { return false; }
        ResolveAfter = null;
        ErrorCode = null;
        BumpVersion();
        return true;
    }
}
