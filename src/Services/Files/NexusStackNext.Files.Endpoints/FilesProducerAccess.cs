using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

namespace NexusStackNext.Files.Endpoints;

/// <summary>Files 内部协议的真实 TLS 生产者认证；普通请求仍使用宿主默认 Bearer。</summary>
public static class FilesProducerAccess
{
    internal const string Scheme = "files-producer";
    internal const string ProducerClaim = "files:producer";

    /// <summary>显式组装生产者的信任根、请求认证和 Kestrel TLS 策略。</summary>
    /// <param name="builder">所属宿主。</param>
    /// <returns>原宿主构建器。</returns>
    public static WebApplicationBuilder AddFilesProducerAccess(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builder.Configuration.GetValue<bool>("Files:Producer:Enabled")) { return builder; }
        var settings = builder.Configuration.GetSection("Files:Producer").Get<FileProducerOptions>() ?? new();
        builder.Services.AddSingleton(_ => new FileProducerTrust(settings, builder.Environment));
        builder.Services.AddAuthentication().AddCertificate(Scheme);
        builder.Services.AddOptions<CertificateAuthenticationOptions>(Scheme).Configure<FileProducerTrust>((options, trust) =>
        {
            options.AllowedCertificateTypes = CertificateTypes.Chained;
            options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
            options.CustomTrustStore = new X509Certificate2Collection(trust.Roots);
            options.AdditionalChainCertificates = new X509Certificate2Collection(trust.Intermediates);
            options.RevocationMode = trust.RevocationMode;
            options.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            options.Events = new CertificateAuthenticationEvents();
            options.Events.OnCertificateValidated = context =>
            {
                var certificate = context.ClientCertificate;
                var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
                var purposes = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
                var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
                if (constraints?.CertificateAuthority != false || usage is null || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)
                    || purposes is null || !purposes.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2")
                    || !trust.Producers.TryGetValue(certificate.GetCertHashString(HashAlgorithmName.SHA256), out var producer))
                { context.Fail("客户端证书没有生产者许可。"); return Task.CompletedTask; }
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ProducerClaim, producer)], Scheme));
                context.Success();
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy(Scheme, policy =>
            policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(ProducerClaim)));
        builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>, FileProducerHttpsOptions>();
        return builder;
    }
}

internal sealed class FileProducerOptions
{
    public string[] RootCertificatePaths { get; set; } = [];
    public string[] IntermediateCertificatePaths { get; set; } = [];
    public FileProducerCertificate[] Certificates { get; set; } = [];
    public string RevocationMode { get; set; } = "Online";
}

internal sealed class FileProducerCertificate
{
    public string Sha256 { get; set; } = string.Empty;
    public string Producer { get; set; } = string.Empty;
}

internal sealed class FileProducerTrust : IDisposable
{
    public X509Certificate2Collection Roots { get; } = [];
    public X509Certificate2Collection Intermediates { get; } = [];
    public Dictionary<string, string> Producers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public X509RevocationMode RevocationMode { get; }

    public FileProducerTrust(FileProducerOptions options, IHostEnvironment environment)
    {
        if (options.RootCertificatePaths.Length is < 1 or > 8 || options.IntermediateCertificatePaths.Length > 8 || options.Certificates.Length is < 1 or > 32
            || (options.RevocationMode != "Online" && (options.RevocationMode != "NoCheck"
                || (!environment.IsDevelopment() && !environment.IsEnvironment("Testing")))))
        { throw new InvalidOperationException("Files 生产者信任配置无效；生产环境必须启用吊销检查。"); }
        RevocationMode = options.RevocationMode == "Online" ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        try
        {
            foreach (var path in options.RootCertificatePaths)
            {
                var root = X509Certificate2.CreateFromPem(File.ReadAllText(path));
                if (root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
                { root.Dispose(); throw new InvalidOperationException("Files 信任根必须是 CA 证书。"); }
                Roots.Add(root);
            }
            foreach (var path in options.IntermediateCertificatePaths)
            {
                var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
                if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
                { certificate.Dispose(); throw new InvalidOperationException("Files 中间签发证书必须是 CA 证书。"); }
                Intermediates.Add(certificate);
            }
            foreach (var certificate in options.Certificates)
            {
                if (certificate.Sha256.Length != 64 || certificate.Sha256.Any(character => !Uri.IsHexDigit(character))
                    || certificate.Producer.Length is < 1 or > 32 || certificate.Producer.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
                    || !Producers.TryAdd(certificate.Sha256, certificate.Producer))
                { throw new InvalidOperationException("Files 生产者证书映射无效。"); }
            }
        }
        catch { Dispose(); throw; }
    }

    public X509ChainPolicy CreatePolicy()
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            VerificationFlags = X509VerificationFlags.NoFlag,
            RevocationMode = RevocationMode,
            RevocationFlag = X509RevocationFlag.ExcludeRoot,
            DisableCertificateDownloads = true,
            UrlRetrievalTimeout = TimeSpan.FromSeconds(2),
        };
        policy.CustomTrustStore.AddRange(Roots);
        policy.ExtraStore.AddRange(Intermediates);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        return policy;
    }

    public void Dispose()
    {
        foreach (var root in Roots) { root.Dispose(); }
        foreach (var certificate in Intermediates) { certificate.Dispose(); }
    }
}

internal sealed class FileProducerHttpsOptions(FileProducerTrust trust) : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions options) => options.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.HandshakeTimeout = TimeSpan.FromSeconds(10);
        https.OnAuthenticate = (_, tls) =>
        {
            tls.AllowTlsResume = false;
            tls.CertificateChainPolicy = trust.CreatePolicy();
        };
    });
}
