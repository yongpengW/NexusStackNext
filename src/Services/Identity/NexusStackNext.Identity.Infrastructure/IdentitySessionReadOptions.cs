namespace NexusStackNext.Identity.Infrastructure;

/// <summary>独立会话权威读取的完整预算，不使用业务 EF 的执行重试。</summary>
public sealed record IdentitySessionReadOptions
{
    /// <summary>包括连接、查询与结果读取的总预算。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>单进程最多同时读取的数量；满时立即拒绝，不排队。</summary>
    public int MaxConcurrency { get; init; } = 16;
    /// <summary>拒绝无限预算或无限并发。</summary>
    public void Validate()
    {
        if (Timeout < TimeSpan.FromMilliseconds(50) || Timeout > TimeSpan.FromSeconds(5) || MaxConcurrency is < 1 or > 128)
        { throw new InvalidOperationException("Identity:SessionAuthority requires Timeout 50ms–5s and MaxConcurrency 1–128."); }
    }
}
