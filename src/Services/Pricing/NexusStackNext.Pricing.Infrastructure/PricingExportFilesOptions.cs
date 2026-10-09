using System.Security.Cryptography.X509Certificates;

namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>仅服务端装配的私有成果 HTTPS 与客户端证书配置。</summary>
public sealed class PricingExportFilesOptions
{
    /// <summary>只允许 HTTPS 的内部 Files 地址。</summary>
    public string BaseAddress { get; set; } = string.Empty;
    /// <summary>带 clientAuth 用途的 PEM 叶证书。</summary>
    public string ClientCertificatePath { get; set; } = string.Empty;
    /// <summary>客户端 PEM 私钥文件；值不进入日志或错误。</summary>
    public string ClientKeyPath { get; set; } = string.Empty;
    /// <summary>明确的服务器私有根集合。</summary>
    public string[] RootCertificatePaths { get; set; } = [];
    /// <summary>明确的服务器中间证书集合。</summary>
    public string[] IntermediateCertificatePaths { get; set; } = [];
    /// <summary>生产默认验证撤销；NoCheck 只限开发或测试环境。</summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.Online;
    /// <summary>单次交付总预算。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}
