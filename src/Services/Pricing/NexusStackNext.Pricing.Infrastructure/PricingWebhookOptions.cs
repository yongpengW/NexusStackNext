namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>Pricing 重算完成通知的显式开关与有界配置。</summary>
public sealed class PricingWebhookOptions
{
    /// <summary>是否把重算完成事件加入本上下文 Outbox。</summary>
    public bool Enabled { get; set; }

    /// <summary>受控接收端地址；启用时必须是绝对 HTTPS 地址。</summary>
    public string? Endpoint { get; set; }

    /// <summary>最大投递尝试次数。</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>校验配置。</summary>
    public void Validate()
    {
        if (!Enabled) { return; }
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("启用 Pricing Webhook 时必须配置 HTTPS Endpoint。");
        }
        if (MaxAttempts is < 1 or > 10) { throw new InvalidOperationException("Pricing Webhook 最大尝试次数必须在 1 至 10 之间。"); }
    }
}
