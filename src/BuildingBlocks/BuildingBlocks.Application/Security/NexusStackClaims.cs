namespace NexusStackNext.BuildingBlocks.Application.Security;

/// <summary>
/// 本系统自定义的令牌声明名。
///
/// <para><b>为什么它们必须是一个共享常量，而不是各写各的字面量。</b>
/// 这几个名字是**签发方与验签方之间的契约**——Identity 签发时写下它们，
/// 网关与各上下文读取时依赖它们。任何一处拼错，症状都是
/// "这个权限明明给了却不生效"，而它看起来像权限配置问题，不像拼写问题。</para>
///
/// <para>它们住在 <c>BuildingBlocks.Application</c> 而不是某个具体项目里，
/// 是因为有三个消费者：Identity 签发、平台宿主读取、网关验签。
/// 不变量 7 说"被第二个消费者证明需要才允许上移"——这里已经有三个。</para>
///
/// <para>标准声明（<c>sub</c>、<c>name</c>、<c>jti</c>）不在这里：
/// 它们由框架定义，我们只是使用。</para>
/// </summary>
public static class NexusStackClaims
{
    /// <summary>是不是根管理员。值是 <c>true</c> / <c>false</c>。</summary>
    public const string Root = "nexusstack:root";

    /// <summary>
    /// 会话版本。撤销一个**已经发出**的访问令牌靠它：
    /// 签发时写下当时的版本，验签方每次比对当前值，对不上就是"已被撤销"。
    /// </summary>
    public const string Session = "nexusstack:session";
}
