using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.HostIntegration.Tests;

internal sealed class GeneratedFileCertificates : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nsn-generated-file-certificates-" + Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _server;
    private readonly X509Certificate2? _intermediate;

    public X509Certificate2 Root { get; }
    public X509Certificate2 Producer { get; }
    internal X509Certificate2 ServerCertificate => _server;
    public IReadOnlyDictionary<string, string> Settings { get; }

    public GeneratedFileCertificates(bool useIntermediate = false)
    {
        Directory.CreateDirectory(_directory);
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=NSN temporary test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(1));
        if (useIntermediate)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=NSN temporary issuing CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var certificate = request.Create(Root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(45), RandomNumberGenerator.GetBytes(16));
            _intermediate = certificate.CopyWithPrivateKey(key);
        }
        _server = CreateLeaf("localhost", "1.3.6.1.5.5.7.3.1", server: true);
        Producer = CreateLeaf("pricing", "1.3.6.1.5.5.7.3.2", server: false, issuer: _intermediate);
        var rootPath = Path.Combine(_directory, "root.pem");
        var serverPath = Path.Combine(_directory, "server.pem");
        var serverKeyPath = Path.Combine(_directory, "server-key.pem");
        File.WriteAllText(rootPath, Root.ExportCertificatePem());
        File.WriteAllText(serverPath, _server.ExportCertificatePem());
        using var serverKey = _server.GetRSAPrivateKey()!;
        File.WriteAllText(serverKeyPath, serverKey.ExportPkcs8PrivateKeyPem());
        var settings = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Testing",
            ["Kestrel__Certificates__Default__Path"] = serverPath,
            ["Kestrel__Certificates__Default__KeyPath"] = serverKeyPath,
            ["Files__Producer__Enabled"] = "true",
            ["Files__Producer__RootCertificatePaths__0"] = rootPath,
            ["Files__Producer__Certificates__0__Sha256"] = Producer.GetCertHashString(HashAlgorithmName.SHA256),
            ["Files__Producer__Certificates__0__Producer"] = "pricing",
            ["Files__Producer__RevocationMode"] = "NoCheck",
        };
        if (_intermediate is not null)
        {
            var path = Path.Combine(_directory, "intermediate.pem");
            File.WriteAllText(path, _intermediate.ExportCertificatePem());
            settings["Files__Producer__IntermediateCertificatePaths__0"] = path;
        }
        Settings = settings;
    }

    public SocketsHttpHandler CreateHandler(X509Certificate2? certificate = null)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            VerificationFlags = X509VerificationFlags.NoFlag,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
            UrlRetrievalTimeout = TimeSpan.FromSeconds(2),
        };
        policy.CustomTrustStore.Add(Root);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        var handler = new SocketsHttpHandler();
        handler.SslOptions.CertificateChainPolicy = policy;
        if (certificate is not null)
        {
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
            // Force this credential onto the wire; a missing-credential HTTP 403 cannot prove TLS chain rejection.
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }
        return handler;
    }

    public X509Certificate2 CreateUnapprovedProducer() => CreateLeaf("unapproved", "1.3.6.1.5.5.7.3.2", server: false);

    public PricingExportFilesOptions PricingClientOptions(Uri baseAddress)
    {
        var certificatePath = Path.Combine(_directory, "pricing-client.pem");
        var keyPath = Path.Combine(_directory, "pricing-client-key.pem");
        File.WriteAllText(certificatePath, Producer.ExportCertificatePem());
        using var key = Producer.GetRSAPrivateKey()!;
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return new PricingExportFilesOptions
        {
            BaseAddress = baseAddress.AbsoluteUri,
            ClientCertificatePath = certificatePath,
            ClientKeyPath = keyPath,
            RootCertificatePaths = [Settings["Files__Producer__RootCertificatePaths__0"]],
            RevocationMode = X509RevocationMode.NoCheck,
        };
    }

    public X509Certificate2 CreateRejectedProducer(string reason) => reason switch
    {
        "expired" => CreateLeaf("expired", "1.3.6.1.5.5.7.3.2", false, expired: true),
        "server-purpose" => CreateLeaf("server-purpose", "1.3.6.1.5.5.7.3.1", false),
        "missing-purpose" => CreateLeaf("missing-purpose", null, false),
        "missing-key-usage" => CreateLeaf("missing-key-usage", "1.3.6.1.5.5.7.3.2", false, includeUsage: false),
        _ => throw new ArgumentException("Unsupported test certificate.", nameof(reason)),
    };

    private X509Certificate2 CreateLeaf(string name, string? purpose, bool server, X509Certificate2? issuer = null, bool expired = false, bool includeUsage = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        if (includeUsage) { request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)); }
        if (purpose is not null) { request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(purpose) }, true)); }
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var certificate = request.Create(issuer ?? Root, expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddMinutes(-1),
            expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
        using var withKey = certificate.CopyWithPrivateKey(key);
        // Windows Schannel needs an imported key container; CopyWithPrivateKey's ephemeral key is insufficient.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        Producer.Dispose();
        _server.Dispose();
        _intermediate?.Dispose();
        Root.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
