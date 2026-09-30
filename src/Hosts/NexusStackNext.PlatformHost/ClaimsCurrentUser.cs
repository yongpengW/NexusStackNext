using System.Globalization;
using System.Security.Claims;
using NexusStackNext.BuildingBlocks.Application.Security;

namespace NexusStackNext.PlatformHost;

/// <summary>从已认证的 ClaimsPrincipal 读当前用户。</summary>
/// <remarks>
/// <para><b>它不解析令牌</b>（ADR-0003）：认证由验签方完成——网关先验一次，
/// 上下文再验一次（因为上下文可能被直连，虽然部署上不该发生）。
/// 这个类只负责把**已经验过的声明**翻译成本层的形状。</para>
/// </remarks>
public sealed class ClaimsCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    // 声明名是签发方与验签方之间的契约，所以它住在 BuildingBlocks（三个消费者）。

    /// <inheritdoc />
    public string? UserId =>
        accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? accessor.HttpContext?.User.FindFirstValue("sub");

    /// <inheritdoc />
    public bool IsRoot =>
        accessor.HttpContext?.User.FindFirstValue(NexusStackClaims.Root) is { } value
        && bool.TryParse(value, out var isRoot)
        && isRoot;

    /// <inheritdoc />
    public long? SessionVersion =>
        accessor.HttpContext?.User.FindFirstValue(NexusStackClaims.Session) is { } value
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
}
