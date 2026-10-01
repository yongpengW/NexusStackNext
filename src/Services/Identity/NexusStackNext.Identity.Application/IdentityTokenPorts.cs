using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Application;

/// <summary>访问令牌的签发结果。</summary>
/// <param name="Token">令牌本身。</param>
/// <param name="ExpiresAt">到期时刻。</param>
public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// 签发访问令牌。
///
/// <para>形态见 ADR-0014：**令牌由本上下文签发，验签交给框架**
/// （<c>Microsoft.AspNetCore.Authentication.JwtBearer</c>）。
/// 我们负责的是生命周期——签发、轮换、撤销、多端——而不是手写签名算法。</para>
/// </summary>
public interface IAccessTokenIssuer
{
    /// <summary>签发一个访问令牌。</summary>
    /// <param name="userId">用户标识。</param>
    /// <param name="userName">用户名（放进令牌便于诊断，不用于授权判定）。</param>
    /// <param name="sessionVersion">会话版本——它让"撤销一个已发出的令牌"成为可能（票据 11）。</param>
    /// <param name="isRoot">
    /// 是不是内置根账号。它会被写成一个声明，而 <c>ICurrentUser.IsRoot</c> 读的正是它。
    ///
    /// <para><b>为什么可以放进令牌，而权限键刻意不放</b>（见 <c>JwtAccessTokenIssuer</c> 的注释）：
    /// "这个账号是不是内置的"从创建那一刻起**不再改变**——它是一个事实，不是一份授权；
    /// 而权限会变，所以权限必须每次请求回源查。</para>
    /// </param>
    /// <param name="now">签发时刻。</param>
    /// <returns>令牌与到期时刻。</returns>
    Result<IssuedAccessToken> Issue(
        UserId userId,
        string userName,
        long sessionVersion,
        bool isRoot,
        DateTimeOffset now);
}

/// <summary>生成不可猜测的秘密串（刷新令牌的原文）。</summary>
public interface ISecretGenerator
{
    /// <summary>生成一个秘密串。</summary>
    /// <returns>足够熵的随机串。</returns>
    string Create();
}

/// <summary>
/// 给秘密串算哈希。
///
/// <para><b>它刻意与 <see cref="IPasswordHasher"/> 分开，理由不是洁癖。</b>
/// 口令哈希必须**每次加盐、故意慢**（PBKDF2 21 万次迭代），因为口令熵低、要抗离线爆破；
/// 而刷新令牌要**按哈希查回来**——那就必须**确定性**，同样的输入每次得到同样的结果。
/// 拿口令哈希去存刷新令牌，第二次刷新就再也查不到它了。</para>
///
/// <para>刷新令牌的原文有 256 位随机熵，不需要慢哈希来抗爆破；
/// 用 SHA-256 就够，它的作用只是"库里不出现原文"。</para>
/// </summary>
public interface ISecretHasher
{
    /// <summary>算哈希。<b>同样的输入必须得到同样的输出。</b></summary>
    /// <param name="secret">秘密串原文。</param>
    /// <returns>编码后的哈希。</returns>
    string Hash(string secret);
}

/// <summary>刷新令牌仓储。</summary>
public interface IRefreshTokenRepository
{
    /// <summary>
    /// **按哈希**查找——这是唯一一条入口。
    /// <para>刻意不提供"按原文查找"：那要求仓储认识原文，而原文不该离开应用层。</para>
    /// </summary>
    /// <param name="tokenHash">令牌哈希。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌；不存在时为 <c>null</c>。</returns>
    Task<RefreshToken?> FindByHashAsync(TokenHash tokenHash, CancellationToken cancellationToken = default);

    /// <summary>登记新令牌或已跟踪令牌的改动；持久化由 Identity 工作单元完成。</summary>
    /// <param name="token">令牌聚合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登记某个用户的全部未撤销令牌的撤销改动；持久化由 Identity 工作单元完成。
    ///
    /// <para>改密码、禁用账号、以及"检测到重放"时调用。它不是锦上添花：
    /// 不撤销全部的话，"改完密码旧令牌还能用"就是一条没人会发现的洞。</para>
    /// </summary>
    /// <param name="userId">用户标识。</param>
    /// <param name="now">撤销时刻。</param>
    /// <param name="reason">原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被撤销的数量。</returns>
    Task<int> RevokeAllAsync(
        UserId userId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>一对令牌——**这是唯一一次原文出现的地方**。</summary>
/// <param name="AccessToken">访问令牌。</param>
/// <param name="AccessTokenExpiresAt">访问令牌到期时刻。</param>
/// <param name="RefreshToken">**刷新令牌原文**。它只在这里存在；库里只有哈希。</param>
/// <param name="RefreshTokenExpiresAt">刷新令牌到期时刻。</param>
public sealed record TokenPair(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
