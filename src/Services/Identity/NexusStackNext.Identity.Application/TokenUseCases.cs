using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Application;

/// <summary>令牌的有效期策略。</summary>
public sealed record TokenLifetimePolicy
{
    /// <summary>访问令牌活多久。它短是**有意的**——撤销的代价与它成正比。</summary>
    public TimeSpan AccessToken { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>刷新令牌活多久。</summary>
    public TimeSpan RefreshToken { get; init; } = TimeSpan.FromDays(14);

    /// <summary>默认策略。</summary>
    public static TokenLifetimePolicy Default { get; } = new();
}

/// <summary>
/// 令牌的签发与轮换。**参照仓库在这三件事上都错了**（review/01）：
/// 明文存储、可重放、撤销不生效。
/// </summary>
/// <param name="users">用户仓储。</param>
/// <param name="refreshTokens">刷新令牌仓储。</param>
/// <param name="accessTokens">访问令牌签发。</param>
/// <param name="secrets">秘密串生成。</param>
/// <param name="hasher">秘密串哈希。</param>
/// <param name="ids">标识生成。</param>
/// <param name="clock">时钟。</param>
/// <param name="transaction">命令提交边界。</param>
/// <param name="policy">有效期策略；为 <c>null</c> 时用默认。</param>
public sealed class TokenIssuer(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IAccessTokenIssuer accessTokens,
    ISecretGenerator secrets,
    ISecretHasher hasher,
    IIdGenerator ids,
    IClock clock,
    IdentityCommandTransaction transaction,
    TokenLifetimePolicy? policy = null)
{
    private readonly TokenLifetimePolicy _policy = policy ?? TokenLifetimePolicy.Default;

    /// <summary>给一个用户签发一对新令牌。</summary>
    /// <param name="user">用户。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌对。</returns>
    public async Task<Result<TokenPair>> IssueAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var prepared = Prepare(user);
        if (prepared.IsFailure)
        {
            return Result.Failure<TokenPair>(prepared.Error);
        }

        await refreshTokens.AddAsync(prepared.Value.Refresh, cancellationToken).ConfigureAwait(false);
        return Result.Success(prepared.Value.Pair);
    }

    private Result<PreparedTokens> Prepare(User user)
    {
        var now = clock.UtcNow;

        // **把当前的会话版本烤进令牌。** 之后任何一次撤销都会让它对不上号，
        // 而验签方不需要查任何名单——这正是无状态令牌能提前失效的唯一办法。
        //
        // `user.IsBuiltIn` 一并带上：它是**根账号的那条旁路**（`ICurrentUser.IsRoot`）的唯一来源。
        // 它此前没有传——于是即使种出了内置根账号，真实 HTTP 上 `IsRoot` 也永远是 false。
        var access = accessTokens.Issue(
            user.Id,
            user.UserName.Value,
            user.SessionVersion,
            user.IsBuiltIn,
            now);
        if (access.IsFailure)
        {
            return Result.Failure<PreparedTokens>(access.Error);
        }

        // **原文只在这里存在一次。** 进聚合之前先换哈希——聚合里根本没有"原文"这个字段。
        var raw = secrets.Create();
        var hash = TokenHash.Create(hasher.Hash(raw));
        if (hash.IsFailure)
        {
            return Result.Failure<PreparedTokens>(hash.Error);
        }

        var refresh = RefreshToken.Issue(
            new RefreshTokenId(ids.NextId()),
            user.Id,
            hash.Value,
            now,
            _policy.RefreshToken,
            user.SessionVersion);

        if (refresh.IsFailure)
        {
            return Result.Failure<PreparedTokens>(refresh.Error);
        }

        return Result.Success(new PreparedTokens(new TokenPair(
            access.Value.Token,
            access.Value.ExpiresAt,
            raw,
            refresh.Value.ExpiresAt), refresh.Value));
    }

    /// <summary>
    /// **轮换**：拿一个刷新令牌换一对新的，旧的那个当场作废。
    ///
    /// <para>两次使用同一个刷新令牌，第二次必须失败——这条是"可重放"的解药（验收 1）。
    /// 而**第一次签发的访问令牌不受影响**：它到期之前仍然有效，
    /// 因为轮换管的是刷新链，不是撤回已经发出去的访问权（那要等票据 11 的撤销机制）。</para>
    ///
    /// <para><b>重放要撤销整条链。</b>如果有人拿着一个**已经用过**的刷新令牌来换，
    /// 那有两种可能：客户端重试，或者令牌被偷了。分不出来——所以按坏的那种处理：
    /// 撤销该用户的全部刷新令牌。丢掉一次"客户端重试"，换掉一次"攻击者一直能用"。</para>
    /// </summary>
    /// <param name="rawRefreshToken">刷新令牌原文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的一对令牌。</returns>
    public async Task<Result<TokenPair>> RefreshAsync(
        string rawRefreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return Result.Failure<TokenPair>(IdentityErrors.RefreshTokenUnusable("令牌为空"));
        }

        var hash = TokenHash.Create(hasher.Hash(rawRefreshToken));
        if (hash.IsFailure)
        {
            return Result.Failure<TokenPair>(hash.Error);
        }

        var stored = await refreshTokens
            .FindByHashAsync(hash.Value, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return Result.Failure<TokenPair>(IdentityErrors.RefreshTokenUnusable("不存在"));
        }

        var user = await users.FindAsync(stored.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<TokenPair>(IdentityErrors.UserNotFound());
        }

        // 先拒绝旧代凭据；反复重放旧令牌不能撤销后来重新登录取得的新会话。
        if (stored.SessionVersion != user.SessionVersion)
        {
            return Result.Failure<TokenPair>(IdentityErrors.RefreshTokenUnusable("会话已撤销"));
        }

        var now = clock.UtcNow;

        // 已经用过：**按被盗处理**，撤销整条链。
        if (stored.ConsumedAt is not null)
        {
            // 只写用户聚合。两类令牌都携带版本，无须把全部刷新令牌载入同一事务。
            user.RevokeSessions();

            var error = IdentityErrors.RefreshTokenUnusable("已被使用过");
            transaction.PreserveChangesOnRejection(error);
            return Result.Failure<TokenPair>(error);
        }

        if (!stored.IsUsable(now))
        {
            // 已不可用时 Consume 只返回领域错误，不修改状态。
            return Result.Failure<TokenPair>(stored.Consume(now).Error);
        }

        // 被禁用的账号不该靠刷新令牌续命——否则"禁用"只对下一次登录生效。
        if (!user.IsEnabled)
        {
            return Result.Failure<TokenPair>(IdentityErrors.UserDisabled());
        }

        // 签名配置、哈希和有效期先验证完；内存适配器也不能在签发失败时消耗旧令牌。
        var prepared = Prepare(user);
        if (prepared.IsFailure)
        {
            return Result.Failure<TokenPair>(prepared.Error);
        }

        var consumed = stored.Consume(now);
        if (consumed.IsFailure)
        {
            return Result.Failure<TokenPair>(consumed.Error);
        }

        await refreshTokens.AddAsync(prepared.Value.Refresh, cancellationToken).ConfigureAwait(false);

        // **必须保存旧令牌的消费状态。** 不保存的话，它永远"未被使用过"，
        // 于是同一个刷新令牌可以无限次换新——正是参照仓库"可重放"那个缺陷。
        await refreshTokens.AddAsync(stored, cancellationToken).ConfigureAwait(false);

        return Result.Success(prepared.Value.Pair);
    }

    private sealed record PreparedTokens(TokenPair Pair, RefreshToken Refresh);
}

/// <summary>用刷新令牌换一对新令牌。</summary>
/// <param name="RefreshToken">刷新令牌原文。</param>
public sealed record RefreshTokenCommand(string RefreshToken) : ICommand<TokenPair>;

/// <summary>刷新令牌的处理器。</summary>
/// <param name="issuer">令牌签发。</param>
public sealed class RefreshTokenHandler(TokenIssuer issuer)
    : ICommandHandler<RefreshTokenCommand, TokenPair>
{
    /// <inheritdoc />
    public Task<Result<TokenPair>> HandleAsync(
        RefreshTokenCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return issuer.RefreshAsync(command.RefreshToken, cancellationToken);
    }
}
