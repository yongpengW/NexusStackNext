namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>当前单份调查导出的有限执行预算。</summary>
public sealed class AuditExportOptions
{
    /// <summary>一次领取的固定期限，不续租；默认两分钟。</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>每轮自动尝试次数。</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>最大完整 ZIP 字节。</summary>
    public long MaxOutputBytes { get; set; } = 33_554_432;
    /// <summary>宿主私有临时目录。</summary>
    public string? TemporaryDirectory { get; set; }
    /// <summary>后台检查间隔。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>验证明确有限的配置。</summary>
    public void Validate()
    {
        if (LeaseDuration < TimeSpan.FromSeconds(1) || LeaseDuration > TimeSpan.FromMinutes(2) || MaxAttempts is < 1 or > 10
            || MaxOutputBytes is < 1 or > 33_554_432 || PollInterval < TimeSpan.FromMilliseconds(100) || PollInterval > TimeSpan.FromSeconds(30)
            || TemporaryDirectory is not null && !Path.IsPathFullyQualified(TemporaryDirectory))
        { throw new InvalidOperationException("调查导出的执行预算无效。"); }
    }
}
