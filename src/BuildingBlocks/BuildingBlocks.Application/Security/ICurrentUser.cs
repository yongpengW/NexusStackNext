namespace NexusStackNext.BuildingBlocks.Application.Security;

/// <summary>
/// 当前请求的发起者。
///
/// <para><b>为什么是一个可注入的端口，而不是 <c>HttpContext</c>。</b>
/// 审计字段（<c>IAuditedEntity.CreatedBy</c> / <c>UpdatedBy</c>）由基础设施层的拦截器写入，
/// 而拦截器不该知道 HTTP——它同样会跑在后台消费、迁移、种子数据里，
/// 那些场景没有 <c>HttpContext</c> 却有"谁做的"这个事实。</para>
///
/// <para><b>可以为空是有意的。</b>系统自身的动作（迁移、定时任务触发的写入）没有发起者，
/// 那时审计字段留空是**事实**，不是缺失。认证形态定下来之前（票据 10），
/// 默认实现一律返回 <c>null</c>——未定状态的默认表现是"不知道谁做的"，
/// 而不是"随便填一个"。</para>
/// </summary>
public interface ICurrentUser
{
    /// <summary>发起者标识；无发起者时为 <c>null</c>。</summary>
    string? UserId { get; }

    /// <summary>
    /// 当前用户是不是根管理员。
    ///
    /// <para>它来自**已认证的声明**（<c>nexusstack:root</c>），不是从数据库现查的：
    /// 判定要用它，而判定在每个请求上跑。谁有资格成为根管理员，是签发令牌时决定的事。</para>
    /// </summary>
    bool IsRoot { get; }

    /// <summary>
    /// 令牌里带来的会话版本。
    ///
    /// <para><c>null</c> 表示**这个身份不是我们签发的**（换了一种认证方案，或者令牌里根本没有这个声明）。
    /// 过滤器把"没有版本"当成对不上号——<b>fail-closed</b>：认不出来的东西不放行。</para>
    /// </summary>
    long? SessionVersion { get; }
}

/// <summary>默认实现：永远没有发起者。认证接入后由宿主替换。</summary>
public sealed class AnonymousCurrentUser : ICurrentUser
{
    /// <inheritdoc />
    public string? UserId => null;

    /// <inheritdoc />
    public bool IsRoot => false;

    /// <inheritdoc />
    public long? SessionVersion => null;
}
