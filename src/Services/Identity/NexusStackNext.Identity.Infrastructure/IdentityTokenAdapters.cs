using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure.Persistence;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>随机秘密串：32 字节加密学随机，base64url 编码。</summary>
public sealed class RandomSecretGenerator : ISecretGenerator
{
    private const int EntropyBytes = 32;

    /// <inheritdoc />
    public string Create() =>
        // base64url 而不是普通 base64：刷新令牌会被放进 URL 查询串或请求体，
        // `+` 与 `/` 在那些位置需要额外转义，而少一次转义就少一处出错的地方。
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(EntropyBytes));
}

/// <summary>
/// SHA-256 的秘密串哈希。
///
/// <para><b>为什么不是 <c>IPasswordHasher</c>。</b>那个每次加随机盐、故意慢 21 万次迭代——
/// 对低熵的口令是对的，对刷新令牌是错的：加盐之后**同样的输入得到不同的输出**，
/// 而我们要按哈希把令牌查回来。第二次刷新就会查不到。</para>
///
/// <para>刷新令牌原文有 256 位熵，不需要慢哈希抗爆破；SHA-256 在这里的作用只有一个：
/// **让库里不出现原文**。</para>
/// </summary>
public sealed class Sha256SecretHasher : ISecretHasher
{
    /// <inheritdoc />
    public string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }
}

// `JwtOptions` 已上移到 `BuildingBlocks.Application/Security`——
// 签发方（Identity）与验签方（网关）必须就同一组值达成一致，那是两个消费者。

/// <summary>
/// 用 HS256 签发访问令牌。
///
/// <para><b>"自研签发"不意味着手写签名</b>（ADR-0014）：签名与验证都交给
/// <c>System.IdentityModel.Tokens.Jwt</c>（.NET 平台自己的库）。
/// 我们负责的是生命周期——签发、轮换、撤销、多端。</para>
/// </summary>
/// <param name="options">配置。</param>
public sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options) : IAccessTokenIssuer
{
    private readonly JwtOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public Result<IssuedAccessToken> Issue(
        UserId userId,
        string userName,
        long sessionVersion,
        bool isRoot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(userId);

        if (Encoding.UTF8.GetByteCount(_options.SigningKey) < 32)
        {
            // 密钥太短时 HS256 的保护形同虚设，而"能用"会让这个问题一直不被发现。
            // 直接拒绝，让它在启动/第一次签发时就暴露。
            return Result.Failure<IssuedAccessToken>(new Error(
                "identity.token.signing_key_too_short",
                "JWT 签名密钥至少需要 32 字节；请在配置中心或环境变量里配置。"));
        }

        var expiresAt = now + _options.AccessTokenLifetime;

        var claims = new List<System.Security.Claims.Claim>
        {
            // `sub` 是标准的主体声明——验签方不必知道我们的领域类型。
            new(JwtRegisteredClaimNames.Sub, userId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Name, userName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),

            // 会话版本。过滤器每次比对它——版本对不上就是"这个令牌已被撤销"。
            new(
                NexusStackClaims.Session,
                sessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        if (isRoot)
        {
            // **根账号声明。** `ICurrentUser.IsRoot` 读的就是它，而它此前**从来没有被签发过**——
            // 令牌里只有 sub / name / jti / 会话版本，于是 `IsRoot` 在真实 HTTP 上永远是 false，
            // 即使种出了一个内置根账号也走不上那条旁路（票据 67 的第三处断链）。
            //
            // 为什么它**可以**放进令牌，而权限键刻意不放（见下面的注释）：
            // "这个账号是不是内置的"从创建那一刻起不再改变，是一个**事实**；
            // 而权限会变，所以权限必须每次请求回源查。
            claims.Add(new System.Security.Claims.Claim(NexusStackClaims.Root, "true"));
        }

        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
                SecurityAlgorithms.HmacSha256)));

        // **刻意不放权限键。** 权限会变，而令牌一旦签发就改不了——
        // 把权限烤进令牌，等于撤销要等到它过期。判定仍然走每一次请求（票据 11）。
        return Result.Success(new IssuedAccessToken(token, expiresAt));
    }
}

/// <summary>EF Core 刷新令牌仓储。</summary>
/// <param name="context">上下文。</param>
public sealed class EfRefreshTokenRepository(IdentityDbContext context) : IRefreshTokenRepository
{
    /// <inheritdoc />
    public async Task<RefreshToken?> FindByHashAsync(
        TokenHash tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        // 按哈希查——唯一索引 `ux_refresh_tokens_hash` 让它是索引查找而不是扫描。
        return await context.RefreshTokens
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        // 可能是新增，也可能是**更新**（轮换时要把旧令牌标记为已消费）。
        // 用 `Update` 而不是 `Add`：`Add` 会对已跟踪的实体抛异常。
        if (context.Entry(token).State == EntityState.Detached)
        {
            var exists = await context.RefreshTokens
                .AnyAsync(candidate => candidate.Id == token.Id, cancellationToken)
                .ConfigureAwait(false);

            if (exists)
            {
                context.RefreshTokens.Update(token);
            }
            else
            {
                context.RefreshTokens.Add(token);
            }
        }

    }

    /// <inheritdoc />
    public async Task<int> RevokeAllAsync(
        UserId userId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var live = await context.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var token in live)
        {
            token.Revoke(now, reason);
        }

        return live.Count;
    }
}

/// <summary>内存版刷新令牌仓储。</summary>
public sealed class InMemoryRefreshTokenRepository : IRefreshTokenRepository
{
    private readonly IdentityMemorySession _session;
    internal InMemoryRefreshTokenRepository(IdentityMemorySession session) => _session = session;

    /// <inheritdoc />
    public Task<RefreshToken?> FindByHashAsync(
        TokenHash tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        return Task.FromResult(_session.Data(cancellationToken).Tokens.Values.FirstOrDefault(token => token.TokenHash.Equals(tokenHash)));
    }

    /// <inheritdoc />
    public Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        var tokens = _session.Data(cancellationToken).Tokens;
        if (tokens.TryGetValue(token.Id, out var tracked) && !ReferenceEquals(tracked, token))
        {
            throw new InvalidOperationException("该令牌已有工作副本，不能替换为未跟踪状态。");
        }
        tokens[token.Id] = token;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> RevokeAllAsync(
        UserId userId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var live = _session.Data(cancellationToken).Tokens.Values.Where(token => token.UserId.Equals(userId) && token.RevokedAt is null).ToList();

        foreach (var token in live)
        {
            token.Revoke(now, reason);
        }

        return Task.FromResult(live.Count);
    }
}

/// <summary>注册令牌相关的适配器。</summary>
public static class IdentityTokenServiceCollectionExtensions
{
    /// <summary>注册秘密串生成器与哈希器（与存储无关）。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddIdentityTokenSecrets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISecretGenerator, RandomSecretGenerator>();
        services.AddSingleton<ISecretHasher, Sha256SecretHasher>();

        return services;
    }

    /// <summary>注册 JWT 访问令牌签发。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="options">配置。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddIdentityJwtIssuer(this IServiceCollection services, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<IOptions<JwtOptions>>(Options.Create(options));
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();

        return services;
    }

    /// <summary>注册 JWT 签发，配置从 <c>IConfiguration</c> 绑定。</summary>
    /// <remarks>
    /// <para><b>为什么是"惰性绑定 + 启动校验"，而不是在注册时读一次值。</b>
    /// 第一版在注册时就把 <c>GetSection("Jwt").Get&lt;JwtOptions&gt;()</c> 读了——
    /// 于是**任何在之后才加上去的配置源都读不到**（测试里的 <c>ConfigureAppConfiguration</c>
    /// 就是这样，宿主的 AgileConfig 也是）。结果是签发拿到空密钥，
    /// 每一次登录都返回 400，而失败点离原因很远。</para>
    ///
    /// <para><c>ValidateOnStart</c> 把"密钥不合格"从"第一次登录时"提前到**进程启动时**，
    /// 同时绑定发生在启动那一刻——两边的配置源都已经在了。</para>
    /// </remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddIdentityJwtIssuer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection("Jwt"))
            .Validate(
                options => Encoding.UTF8.GetByteCount(options.SigningKey) >= 32,
                "Jwt:SigningKey 至少需要 32 字节。请在配置中心或环境变量里配置，不要放进仓库。")
            .ValidateOnStart();

        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();

        return services;
    }
}
