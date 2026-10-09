using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace NexusStackNext.HostIntegration.Tests;

internal enum GeneratedFileFaultPoint { BeforeContent, AfterPublication }

// Real HTTPS on both sides. The fixture accepts only its exact temporary producer credential;
// the actual Files host still verifies that credential and persists the real operation.
internal sealed class GeneratedFileReplyFault(WebApplication application, HttpClient upstream, GeneratedFileFaultPoint point, byte[] producerHash) : IAsyncDisposable
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed = 1;
    public string LastTransportResult { get; private set; } = "No HTTP request reached relay";
    public Guid ObservedUploadId { get; private set; }
    public Uri BaseAddress { get; private set; } = null!;
    public Task WaitForFaultAsync() => _arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
    public void Release() => _release.TrySetResult();

    public static async Task<GeneratedFileReplyFault> StartAsync(GeneratedFileCertificates certificates, Uri target, GeneratedFileFaultPoint point)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(new HttpsConnectionAdapterOptions
        {
            ServerCertificate = certificates.ServerCertificate,
            ClientCertificateMode = ClientCertificateMode.AllowCertificate,
            OnAuthenticate = (_, tls) =>
            {
                tls.AllowTlsResume = false;
                var policy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    VerificationFlags = X509VerificationFlags.NoFlag,
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true,
                };
                policy.CustomTrustStore.Add(certificates.Root);
                policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
                tls.CertificateChainPolicy = policy;
            },
        })));
        var app = builder.Build();
        var handler = certificates.CreateHandler(certificates.Producer);
        handler.AllowAutoRedirect = false;
        var client = new HttpClient(handler) { BaseAddress = target, Timeout = TimeSpan.FromSeconds(20) };
        var fault = new GeneratedFileReplyFault(app, client, point, certificates.Producer.GetCertHash(HashAlgorithmName.SHA256));
        app.Run(fault.ForwardAsync);
        try
        {
            await app.StartAsync();
            fault.BaseAddress = new Uri(app.Urls.Single());
            return fault;
        }
        catch { await fault.DisposeAsync(); throw; }
    }

    private async Task ForwardAsync(HttpContext context)
    {
        try
        {
            if (context.Connection.ClientCertificate is not { } credential
                || !credential.GetCertHash(HashAlgorithmName.SHA256).AsSpan().SequenceEqual(producerHash))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            var path = context.Request.Path.Value!;
            if (point == GeneratedFileFaultPoint.BeforeContent && HttpMethods.IsPut(context.Request.Method)
                && path.EndsWith("/content", StringComparison.Ordinal) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                ObservedUploadId = Guid.Parse(path.Split('/')[^2]);
                _arrived.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), context.RequestAborted);
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), path + context.Request.QueryString);
            if (HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPost(context.Request.Method))
            {
                request.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType is { } contentType) { request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType); }
            }
            using var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            LastTransportResult = context.Request.Method + " HTTP " + (int)response.StatusCode;
            // Buffer only the protocol reply (up to 16 KiB); artifact bytes pass in the request stream.
            var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
            Assert.InRange(body.Length, 0, 16_384);
            if (point == GeneratedFileFaultPoint.AfterPublication && HttpMethods.IsPost(context.Request.Method)
                && path.EndsWith("/publish", StringComparison.Ordinal) && response.IsSuccessStatusCode && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _arrived.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), context.RequestAborted);
                context.Abort();
                return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or TimeoutException)
        { LastTransportResult = context.Request.Method + " " + error.GetType().Name; context.Abort(); }
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await application.StopAsync(budget.Token); }
        finally { upstream.Dispose(); await application.DisposeAsync(); }
    }
}
