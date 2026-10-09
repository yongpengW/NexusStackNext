using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Pricing.Application;

namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>私有成果适配器的显式宿主装配。</summary>
public static class PricingExportFilesServices
{
    /// <summary>注册仅使用客户端证书认证的 HTTPS 交付。</summary>
    /// <param name="services">容器。</param>
    /// <param name="options">HTTPS 配置。</param>
    /// <param name="environmentName">宿主环境；决定是否允许测试撤销例外。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddPricingExportFiles(this IServiceCollection services, PricingExportFilesOptions options, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        services.AddSingleton<IExportFiles>(_ => new PricingExportFilesClient(options, environmentName));
        return services;
    }
}

internal sealed class PricingExportFilesClient : IExportFiles, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { NumberHandling = JsonNumberHandling.AllowReadingFromString, MaxDepth = 16 };
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    private readonly X509Certificate2 _credential;
    private readonly X509Certificate2Collection _trust = [];

    public PricingExportFilesClient(PricingExportFilesOptions options, string environmentName)
    {
        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0 || address.AbsolutePath != "/"
            || options.RootCertificatePaths.Length is < 1 or > 8 || options.IntermediateCertificatePaths.Length > 8
            || options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromSeconds(60)
            || (options.RevocationMode != X509RevocationMode.Online && (options.RevocationMode != X509RevocationMode.NoCheck
                || environmentName is not ("Development" or "Testing"))))
        { throw new InvalidOperationException("Pricing 私有成果 HTTPS 配置无效。"); }
        _timeout = options.Timeout;
        using var pem = X509Certificate2.CreateFromPemFile(options.ClientCertificatePath, options.ClientKeyPath);
        if (!pem.HasPrivateKey || pem.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != false
            || pem.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault() is not { } usage
            || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)
            || pem.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault() is not { } purposes
            || !purposes.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"))
        { throw new InvalidOperationException("Pricing 客户端证书必须是具有私钥、签名与 clientAuth 用途的叶证书。"); }
        // Windows Schannel 需要导入的密钥容器；不把 PEM 私钥或口令放入配置字符串。
        _credential = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.DefaultKeySet);
        try
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                VerificationFlags = X509VerificationFlags.NoFlag,
                RevocationMode = options.RevocationMode,
                RevocationFlag = X509RevocationFlag.ExcludeRoot,
                DisableCertificateDownloads = true,
                UrlRetrievalTimeout = TimeSpan.FromSeconds(2),
            };
            foreach (var path in options.RootCertificatePaths)
            {
                var root = X509Certificate2.CreateFromPem(File.ReadAllText(path));
                _trust.Add(root);
                if (root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
                { throw new InvalidOperationException("Pricing 服务器信任根必须是 CA。"); }
                policy.CustomTrustStore.Add(root);
            }
            foreach (var path in options.IntermediateCertificatePaths)
            {
                var intermediate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
                _trust.Add(intermediate);
                if (intermediate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
                { throw new InvalidOperationException("Pricing 中间证书必须是 CA。"); }
                policy.ExtraStore.Add(intermediate);
            }
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = 2 };
            // 保留 TLS 原生主机名验证；只替换链信任，不设置放行回调。
            handler.SslOptions.CertificateChainPolicy = policy;
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { _credential };
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => _credential;
            _client = new HttpClient(handler) { BaseAddress = address, Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        }
        catch
        {
            _credential.Dispose();
            foreach (var certificate in _trust) { certificate.Dispose(); }
            throw;
        }
    }

    public async Task<Result<GeneratedFileReceiptV1>> StageAsync(ExportFileUpload upload, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanSeek || !content.CanRead || upload.UploadId == Guid.Empty) { return Invalid<GeneratedFileReceiptV1>(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            var path = $"/internal/files/v1/uploads/{upload.UploadId:D}";
            var found = await RequestAsync<GeneratedFileReceiptV1>(HttpMethod.Get, path, null, budget.Token).ConfigureAwait(false);
            if (found.IsFailure && found.Error.Code != "pricing.export.file_not_found") { return found; }
            if (found.IsFailure)
            {
                // Int64 契约使用十进制字符串；不会经过 JavaScript/浮点数。
                var description = upload.Description;
                using var registration = JsonContent.Create(new
                {
                    description.OwnerId,
                    description.SourceExportId,
                    description.Sha256,
                    Length = description.Length.ToString(CultureInfo.InvariantCulture),
                    description.Format,
                    description.FormatVersion,
                    description.ColumnSetVersion,
                });
                found = await RequestAsync<GeneratedFileReceiptV1>(HttpMethod.Post, path, registration, budget.Token).ConfigureAwait(false);
            }
            if (found.IsFailure) { return found; }
            if (!Matches(found.Value, upload)) { return Invalid<GeneratedFileReceiptV1>(); }
            if (found.Value.Stage == "Staged") { return found; }
            content.Position = 0;
            // StreamContent 释放一个不拥有底层流的包装，生成器始终负责自己的临时文件。
            using var bytes = new StreamContent(new BorrowedReadStream(content));
            var sealedFile = await RequestAsync<GeneratedFileReceiptV1>(HttpMethod.Put, path + "/content", bytes, budget.Token).ConfigureAwait(false);
            return sealedFile.IsFailure ? sealedFile : Matches(sealedFile.Value, upload) && sealedFile.Value.Stage == "Staged"
                ? sealedFile : Invalid<GeneratedFileReceiptV1>();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { cancellationToken.ThrowIfCancellationRequested(); return Unavailable<GeneratedFileReceiptV1>(); }
    }

    public async Task<Result<GeneratedFilePublicationReceiptV1>> PublishAsync(ExportFilePublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (publication.UploadId == Guid.Empty || publication.PublicationId == Guid.Empty || publication.FileId <= 0) { return Invalid<GeneratedFilePublicationReceiptV1>(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            var found = await RequestAsync<GeneratedFilePublicationReceiptV1>(HttpMethod.Get,
                $"/internal/files/v1/publications/{publication.PublicationId:D}", null, budget.Token).ConfigureAwait(false);
            if (found.IsFailure && found.Error.Code != "pricing.export.file_not_found") { return found; }
            if (found.IsFailure)
            {
                using var intent = JsonContent.Create(new GeneratedFilePublicationV1(publication.PublicationId));
                found = await RequestAsync<GeneratedFilePublicationReceiptV1>(HttpMethod.Post,
                    $"/internal/files/v1/uploads/{publication.UploadId:D}/publish", intent, budget.Token).ConfigureAwait(false);
            }
            if (found.IsFailure) { return found; }
            var receipt = found.Value;
            return receipt.Producer == "pricing" && receipt.FileId == publication.FileId && receipt.UploadId == publication.UploadId
                && receipt.PublicationId == publication.PublicationId && receipt.Description == publication.Description
                && receipt.PublishedAt != default && receipt.ExpiresAt > receipt.PublishedAt
                ? found : Invalid<GeneratedFilePublicationReceiptV1>();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { cancellationToken.ThrowIfCancellationRequested(); return Unavailable<GeneratedFilePublicationReceiptV1>(); }
    }

    public async Task<Result<GeneratedFileAvailabilityV1>> AvailabilityAsync(Guid uploadId, long fileId, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            var found = await RequestAsync<GeneratedFileAvailabilityV1>(HttpMethod.Get,
                $"/internal/files/v1/uploads/{uploadId:D}/availability", null, budget.Token).ConfigureAwait(false);
            return found.IsFailure ? found : found.Value.FileId == fileId
                && found.Value.State is "Pending" or "Staged" or "Available" or "Expired" or "Deleted" or "StorageUnavailable"
                ? found : Invalid<GeneratedFileAvailabilityV1>();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { cancellationToken.ThrowIfCancellationRequested(); return Unavailable<GeneratedFileAvailabilityV1>(); }
    }

    private async Task<Result<T>> RequestAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        { return Result.Failure<T>(new Error("pricing.export.file_not_found", "原成果身份不存在。")); }
        if (response.Content.Headers.ContentLength > 16_384) { return Invalid<T>(); }
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) { break; }
            if (body.Length + read > 16_384) { return Invalid<T>(); }
            body.Write(buffer, 0, read);
        }
        using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { return Invalid<T>(); }
        if (!response.IsSuccessStatusCode)
        {
            var code = root.TryGetProperty("errorCode", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            return code is "files.candidate.closed" or "files.candidate.expired" ? Closed<T>()
                : code is "files.candidate.conflict" or "files.publication.conflict" ? Invalid<T>() : Unavailable<T>();
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) { return Invalid<T>(); }
        var value = data.Deserialize<T>(Json);
        return value is null ? Invalid<T>() : Result.Success(value);
    }

    private static bool Matches(GeneratedFileReceiptV1 receipt, ExportFileUpload upload) => receipt.Producer == "pricing" && receipt.FileId > 0
        && receipt.UploadId == upload.UploadId && receipt.Description == upload.Description && receipt.AcceptedAt != default
        && receipt.StageExpiresAt > receipt.AcceptedAt && (receipt.Stage == "Pending" ? receipt.SealedAt is null
            : receipt.Stage == "Staged" && receipt.SealedAt >= receipt.AcceptedAt && receipt.SealedAt < receipt.StageExpiresAt);
    private static Result<T> Invalid<T>() => Result.Failure<T>(new Error("pricing.export.invalid_receipt", "成果回执与原身份或快照不一致。"));
    private static Result<T> Closed<T>() => Result.Failure<T>(new Error("pricing.export.file_closed", "原候选已关闭，请新建导出。"));
    private static Result<T> Unavailable<T>() => Result.Failure<T>(new Error("pricing.export.files_unavailable", "成果服务暂不可用，请按原身份恢复。"));
    public void Dispose()
    {
        _client.Dispose();
        _credential.Dispose();
        foreach (var certificate in _trust) { certificate.Dispose(); }
    }
}

internal sealed class BorrowedReadStream(Stream source) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => source.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => source.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => source.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => source.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
