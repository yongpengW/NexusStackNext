using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Identity.Contracts;

namespace NexusStackNext.Composition;

/// <summary>固定的 Identity 会话权威地址与单次请求预算。</summary>
public sealed record IdentitySessionClientOptions
{
    /// <summary>固定来源，仅允许 HTTP(S) 根地址，不接受凭据、查询、片段或回调路径。</summary>
    public string BaseAddress { get; init; } = string.Empty;
    /// <summary>完整调用预算，包含响应体读取。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>单宿主同时调用的上限；满时不排队。</summary>
    public int MaxConcurrency { get; init; } = 16;
    /// <summary>内网明文传输必须显式接受；HTTPS 始终保留系统证书校验。</summary>
    public bool AllowUnencryptedHttp { get; init; }

    internal Uri Endpoint()
    {
        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0 || address.AbsolutePath != "/"
            || (address.Scheme == "http" && !address.IsLoopback && !AllowUnencryptedHttp)
            || Timeout < TimeSpan.FromMilliseconds(50) || Timeout > TimeSpan.FromSeconds(10) || MaxConcurrency is < 1 or > 128)
        { throw new InvalidOperationException("IdentitySession requires a fixed trusted origin, Timeout 50ms–10s and MaxConcurrency 1–128; non-loopback HTTP requires explicit opt-in."); }
        return new(address, "api/identity/session/v1");
    }
}

internal sealed class IdentitySessionCallBudget(IdentitySessionClientOptions options) : IDisposable
{
    internal SemaphoreSlim Permits { get; } = new(options.MaxConcurrency, options.MaxConcurrency);
    public void Dispose() => Permits.Dispose();
}

internal sealed class IdentityAuthorityReader(IHttpClientFactory clients, IHttpContextAccessor accessor,
    IdentitySessionClientOptions options, IdentitySessionCallBudget calls)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    internal async Task<Result<T>> ReadAsync<T>(Uri endpoint, long? sessionVersion, CancellationToken cancellationToken) where T : class
    {
        var http = accessor.HttpContext;
        if (sessionVersion is null or < 0 || http?.User.Identity?.IsAuthenticated != true
            || http.Request.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization.ToString(), out var bearer)
            || !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(bearer.Parameter))
        { return Result.Failure<T>(SessionValidationErrors.Invalid); }
        if (!await calls.Permits.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { return Unavailable<T>(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = bearer;
            using var response = await clients.CreateClient("identity-session-authority").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) { return Result.Failure<T>(SessionValidationErrors.Invalid); }
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 4096
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)) { return Unavailable<T>(); }
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            var bytes = new byte[4097];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), budget.Token).ConfigureAwait(false);
                if (read == 0) { break; }
                count += read;
            }
            if (count > 4096) { return Unavailable<T>(); }
            using var body = JsonDocument.Parse(bytes.AsMemory(0, count));
            var envelope = body.RootElement;
            if (!envelope.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                || !envelope.TryGetProperty("code", out var code) || !code.TryGetInt32(out var status) || status != 200
                || !envelope.TryGetProperty("data", out var data)) { return Unavailable<T>(); }
            var decision = data.Deserialize<T>(Json);
            budget.Token.ThrowIfCancellationRequested();
            return decision is not null ? Result.Success(decision) : Unavailable<T>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable<T>(); }
        catch (Exception error) when (error is HttpRequestException or IOException) { cancellationToken.ThrowIfCancellationRequested(); return Unavailable<T>(); }
        catch (JsonException) { return Unavailable<T>(); }
        catch (InvalidOperationException) { return Unavailable<T>(); }
        finally { calls.Permits.Release(); }
    }

    private static Result<T> Unavailable<T>() => Result.Failure<T>(SessionValidationErrors.Unavailable);
}

internal sealed class HttpSessionValidator(IdentityAuthorityReader authority, IdentitySessionClientOptions options) : ISessionValidator
{
    public async Task<Result<ValidatedSession>> ValidateAsync(string userId, long? sessionVersion, CancellationToken cancellationToken = default)
    {
        var response = await authority.ReadAsync<CurrentSessionV1>(options.Endpoint(), sessionVersion, cancellationToken).ConfigureAwait(false);
        if (response.IsFailure) { return Result.Failure<ValidatedSession>(response.Error); }
        var decision = response.Value;
        return decision.ContractVersion == 1 && decision.Subject == userId && decision.SessionVersion == sessionVersion
            ? Result.Success(new ValidatedSession(decision.IsRoot)) : Result.Failure<ValidatedSession>(SessionValidationErrors.Unavailable);
    }
}

/// <summary>三个独立宿主以同一个版本化 HTTP 契约消费 Identity；不引用其存储或领域。</summary>
public static class IdentitySessionAuthorizationServices
{
    /// <summary>显式配置固定权威来源；凭据仅来自当前请求，不进入全局客户端头或后台。</summary>
    /// <param name="services">宿主服务。</param>
    /// <param name="configuration">宿主配置。</param>
    /// <returns>原集合。</returns>
    public static IServiceCollection AddIdentitySessionAuthority(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection("IdentitySession").Get<IdentitySessionClientOptions>() ?? new();
        _ = options.Endpoint();
        services.AddSingleton(options);
        services.AddSingleton<IdentitySessionCallBudget>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        services.AddScoped<IdentityAuthorityReader>();
        services.AddScoped<ISessionValidator, HttpSessionValidator>();
        services.AddScoped<IRequestAccessValidator, HttpRequestAccessValidator>();
        services.AddHttpClient("identity-session-authority", client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(["Authorization"])
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxConnectionsPerServer = options.MaxConcurrency,
                ConnectTimeout = options.Timeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });
        return services.AddCurrentSessionAuthorization();
    }
}
