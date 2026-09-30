namespace NexusStackNext.BuildingBlocks.Application.Security;

/// <summary>
/// 令牌的签发与验签配置。
///
/// <para><b>它为什么在 BuildingBlocks 而不是 Identity 里。</b>签发方（Identity）
/// 与验签方（网关）**必须就同一组值达成一致**——签名密钥、签发者、受众，
/// 任何一处不同，症状都是"令牌明明签出来了却验不过"。那个症状看起来像认证配置问题，
/// 不像"两边用了不同的默认值"。</para>
///
/// <para>不变量 7 说"被第二个消费者证明需要才允许上移"——签发与验签正好是两个。</para>
/// </summary>
public sealed record JwtOptions
{
    /// <summary>签发者。签发与验签必须一致。</summary>
    public string Issuer { get; init; } = "nexusstack";

    /// <summary>受众。签发与验签必须一致。</summary>
    public string Audience { get; init; } = "nexusstack";

    /// <summary>
    /// 签名密钥。**必须来自配置中心或环境变量，不进仓库**（HS256 要求至少 32 字节）。
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>访问令牌有效期。它短是**有意的**——撤销的代价与它成正比。</summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
}
