using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NexusStackNext.Gateway;

/// <summary>网关的认证方案名。</summary>
public static class GatewayAuthentication
{
    /// <summary>
    /// "认证尚未配置"的方案名。票据 10 决定认证形态后，真实的处理器会替换它。
    /// </summary>
    public const string NotConfiguredScheme = "gateway.not-configured";
}

/// <summary>
/// <b>永远不认证</b>的处理器。
/// <para>
/// 它存在的意义是让"认证形态未定"这件事有一个**安全**的默认表现：
/// 受保护路由挂的是 <c>RequireAuthenticatedUser()</c> 策略，而这个处理器永远返回
/// <see cref="AuthenticateResult.NoResult"/>，于是用户始终未认证、策略始终失败、请求得到 401。
/// </para>
/// <para>
/// 换句话说：<b>忘了配置认证，结果是"全都进不去"，不是"全都进得来"</b>。
/// 参照仓库在这一点上正好相反（见 ADR-0010）。
/// </para>
/// </summary>
/// <param name="options">方案配置。</param>
/// <param name="loggerFactory">日志工厂。</param>
/// <param name="encoder">URL 编码器。</param>
public sealed class NotConfiguredAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // NoResult 而不是 Fail：这不是"认证失败"，而是"这里根本没有认证"。
        // 结果是用户未认证 —— 受保护路由因此必然被拒。
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}
