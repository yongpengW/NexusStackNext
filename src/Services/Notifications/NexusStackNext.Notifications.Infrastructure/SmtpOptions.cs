using MailKit.Security;
using MimeKit;

namespace NexusStackNext.Notifications.Infrastructure;

/// <summary>宿主显式提供的 SMTP 配置；认证值只来自私有配置。</summary>
public sealed class SmtpOptions
{
    /// <summary>固定邮件服务器。</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>服务器端口。</summary>
    public int Port { get; set; } = 587;
    /// <summary>默认强制 STARTTLS；None 仅由开发测试宿主开放。</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;
    /// <summary>固定发件地址，提交者不能覆盖。</summary>
    public string Sender { get; set; } = string.Empty;
    /// <summary>可选 SMTP 认证用户。</summary>
    public string? UserName { get; set; }
    /// <summary>可选 SMTP 认证口令，不进入状态、异常或观察。</summary>
    public string? Password { get; set; }

    /// <summary>验证可执行配置，不输出配置值。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 || !MailboxAddress.TryParse(Sender, out _)
            || Security is not (SecureSocketOptions.None or SecureSocketOptions.StartTls or SecureSocketOptions.SslOnConnect)
            || string.IsNullOrEmpty(UserName) != string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException("必须配置有效的 Notifications:Smtp 服务器、发件人、强制TLS方式及成对认证信息。");
        }
    }
}
