using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>登出：撤销这个用户已发出的全部访问令牌与刷新令牌。</summary>
/// <param name="UserId">用户标识——由已认证的身份给出，不由请求体。</param>
public sealed record LogoutCommand(long UserId) : ICommand, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>
/// 登出。
///
/// <para><b>它必须同时做两件事</b>，而且理由值得写下来：</para>
///
/// <list type="bullet">
/// <item>撤销**刷新令牌**（票据 10 的路径）：不做的话，客户端拿手上的刷新令牌
/// 又能换一对新的——登出等于没登。</item>
/// <item>涨**会话版本**（票据 11 的路径）：不做的话，手上那个访问令牌还能一直用到过期
/// （默认 15 分钟）——而"我登出了"与"别人还能用我的身份"之间的那 15 分钟，
/// 正是登出想要消灭的东西。</item>
/// </list>
///
/// <para>两条路径互不替代。**要真的赶走一个人，两个都要做。**</para>
/// </summary>
/// <param name="tokens">刷新令牌仓储。</param>
/// <param name="transaction">提交边界，在持久化成功后撤销会话。</param>
/// <param name="clock">时钟。</param>
public sealed class LogoutHandler(
    IRefreshTokenRepository tokens,
    IdentityCommandTransaction transaction,
    BuildingBlocks.Application.Time.IClock clock) : ICommandHandler<LogoutCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        LogoutCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var userId = new Domain.Ids.UserId(command.UserId);

        await tokens
            .RevokeAllAsync(userId, clock.UtcNow, "用户登出", cancellationToken)
            .ConfigureAwait(false);

        transaction.RevokeSessionAfterCommit(command.UserId);

        // 幂等：重复登出算成功。登出不是"改变什么"，而是"确保不再有效"——
        // 已经无效时它的目的已经达到了，返回失败只会让客户端困惑。
        return Result.Success();
    }
}
