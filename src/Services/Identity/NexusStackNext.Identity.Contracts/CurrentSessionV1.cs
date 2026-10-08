using System.Text.Json.Serialization;

namespace NexusStackNext.Identity.Contracts;

/// <summary>Identity 对当前已验签调用者的会话事实；只供本次授权使用。</summary>
/// <param name="ContractVersion">协议版本，固定为 1。</param>
/// <param name="Subject">已认证主体。</param>
/// <param name="SessionVersion">当前仍被接受的会话版本。</param>
/// <param name="IsRoot">权威用户是否为内置根身份。</param>
public sealed record CurrentSessionV1(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string Subject,
    [property: JsonRequired] long SessionVersion,
    [property: JsonRequired] bool IsRoot);
