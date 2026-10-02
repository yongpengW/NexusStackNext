using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NexusStackNext.BuildingBlocks.Application.Security;

namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>仅从已经认证的 HTTP 声明读取当前操作者；后台作用域返回空。</summary>
/// <param name="accessor">当前请求访问器。</param>
public sealed class ClaimsCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } principal ? principal : null;

    /// <inheritdoc />
    public string? UserId => Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Principal?.FindFirstValue("sub");

    /// <inheritdoc />
    public bool IsRoot => bool.TryParse(Principal?.FindFirstValue(NexusStackClaims.Root), out var isRoot) && isRoot;

    /// <inheritdoc />
    public long? SessionVersion => long.TryParse(Principal?.FindFirstValue(NexusStackClaims.Session),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : null;
}
