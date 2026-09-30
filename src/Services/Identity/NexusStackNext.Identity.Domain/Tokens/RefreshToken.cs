using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Events;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Tokens;

/// <summary>
/// 刷新令牌聚合根。
/// <para>
/// <b>这个类里没有任何明文字段。</b>只有 <see cref="TokenHash"/>——明文令牌只在签发的那一刻
/// 存在于返回值里，之后服务端再也拿不到它。参照仓库把令牌明文入库
/// （<c>UserTokenService</c>），于是一条本该一次性的凭据变成了可长期重放的密钥；
/// 而且"校验后再作废"跨了三个 <c>await</c> 且没有并发保护，同一条令牌能被用两次。
/// </para>
/// <para>一次性语义由 <see cref="ConsumedAt"/> 承载；真正的并发安全还要靠数据库并发令牌
/// （票据 19），二者缺一不可：领域负责语义，数据库负责原子性。</para>
/// </summary>
public sealed class RefreshToken : AggregateRoot<RefreshTokenId>
{
    private RefreshToken(
        RefreshTokenId id,
        UserId userId,
        TokenHash tokenHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>所属用户。</summary>
    public UserId UserId { get; }

    /// <summary>令牌哈希。<b>没有明文令牌属性，这是结构性的保证。</b></summary>
    public TokenHash TokenHash { get; }

    /// <summary>签发时刻。</summary>
    public DateTimeOffset IssuedAt { get; }

    /// <summary>到期时刻。</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>被消费的时刻；<c>null</c> 表示尚未使用。</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>被撤销的时刻；<c>null</c> 表示未撤销。</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>撤销原因。</summary>
    public string? RevokedReason { get; private set; }

    /// <summary>签发一条刷新令牌。</summary>
    /// <param name="id">标识。</param>
    /// <param name="userId">所属用户。</param>
    /// <param name="tokenHash">令牌哈希。</param>
    /// <param name="issuedAt">签发时刻。</param>
    /// <param name="lifetime">有效期。</param>
    /// <returns>成功时返回令牌；有效期不为正则失败。</returns>
    public static Result<RefreshToken> Issue(
        RefreshTokenId id,
        UserId userId,
        TokenHash tokenHash,
        DateTimeOffset issuedAt,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(tokenHash);

        if (lifetime <= TimeSpan.Zero)
        {
            return Result.Failure<RefreshToken>(new Error(
                "identity.refresh_token.invalid_lifetime",
                "刷新令牌有效期必须为正。"));
        }

        var token = new RefreshToken(id, userId, tokenHash, issuedAt, issuedAt + lifetime);
        token.Raise(new RefreshTokenIssued(id, userId, issuedAt));
        return Result.Success(token);
    }

    /// <summary>此刻是否可用（未消费、未撤销、未过期）。</summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>是否可用。</returns>
    public bool IsUsable(DateTimeOffset now) =>
        ConsumedAt is null && RevokedAt is null && now < ExpiresAt;

    /// <summary>
    /// 消费这条令牌（一次性）。
    /// <para>重复消费返回失败——调用方据此识别重放，并应撤销同一用户的整条令牌链。</para>
    /// </summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>成功，或不可用的原因。</returns>
    public Result Consume(DateTimeOffset now)
    {
        if (ConsumedAt is { } consumedAt)
        {
            return Result.Failure(IdentityErrors.RefreshTokenUnusable($"已于 {consumedAt:u} 使用过"));
        }

        if (RevokedAt is not null)
        {
            return Result.Failure(IdentityErrors.RefreshTokenUnusable("已撤销"));
        }

        if (now >= ExpiresAt)
        {
            return Result.Failure(IdentityErrors.RefreshTokenUnusable($"已于 {ExpiresAt:u} 过期"));
        }

        ConsumedAt = now;
        return Changed();
    }

    /// <summary>撤销令牌。可重复调用（幂等），不重复发事件。</summary>
    /// <param name="now">当前时刻。</param>
    /// <param name="reason">撤销原因。</param>
    /// <returns>成功。</returns>
    public Result Revoke(DateTimeOffset now, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (RevokedAt is not null)
        {
            return Result.Success();
        }

        RevokedAt = now;
        RevokedReason = reason;
        Raise(new RefreshTokenRevoked(Id, UserId, reason, now));
        return Changed();
    }
}
