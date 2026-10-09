using System.Text.Json.Serialization;

namespace NexusStackNext.Identity.Contracts;

/// <summary>绑定主体、会话及单个操作的本次判定；不得用于后续请求或替代对象规则。</summary>
/// <param name="ContractVersion">固定为 1。</param>
/// <param name="Subject">已认证主体。</param>
/// <param name="SessionVersion">当前会话版本。</param>
/// <param name="PermissionKey">本次查询的规范权限键。</param>
/// <param name="IsAllowed">当前用户状态及权限链给出的结论。</param>
public sealed record CurrentAccessV1(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string Subject,
    [property: JsonRequired] long SessionVersion,
    [property: JsonRequired] string PermissionKey,
    [property: JsonRequired] bool IsAllowed);
