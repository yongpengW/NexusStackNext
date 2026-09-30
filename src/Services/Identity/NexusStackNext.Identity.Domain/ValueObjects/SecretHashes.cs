using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Domain.ValueObjects;

/// <summary>
/// 已编码机密（密码哈希、令牌哈希）的公共规则。
/// <para>
/// <b>这一层存在的唯一理由是：明文不该有容身之处。</b>参照仓库把刷新令牌明文入库
/// （<c>UserTokenService</c>），一条本该"一次使用"的凭据变成了可重放的长期口令。
/// 这里要求值必须是**已编码的哈希**形态（长度下限 + 无空白），
/// 于是"忘记哈希就存进去"会在构造处就被拒绝，而不是等到数据泄露时才被发现。
/// </para>
/// <para>长度下限并不能证明它真的是哈希——真正的保证来自领域对象的字段里根本没有明文字段。</para>
/// </summary>
public abstract class SecretHash : ValueObject
{
    /// <summary>已编码哈希的最小长度。bcrypt 是 60，PBKDF2 base64 通常 44+，SHA-256 十六进制是 64。</summary>
    public const int MinLength = 32;

    /// <summary>构造已编码机密。</summary>
    /// <param name="encoded">已编码的值。</param>
    protected SecretHash(string encoded) => Encoded = encoded;

    /// <summary>已编码的值。</summary>
    public string Encoded { get; }

    /// <summary>校验编码形态是否像哈希。</summary>
    /// <param name="encoded">待校验的值。</param>
    /// <param name="codePrefix">错误码前缀。</param>
    /// <param name="fieldName">字段名，用于错误信息。</param>
    /// <returns>错误；通过则为 <c>null</c>。</returns>
    protected static Error? ValidateEncoding(string? encoded, string codePrefix, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return new Error($"{codePrefix}.empty", $"{fieldName}不能为空。");
        }

        if (encoded.Length < MinLength)
        {
            return new Error(
                $"{codePrefix}.too_short",
                $"{fieldName}长度不足 {MinLength}，看起来不像已编码的哈希。");
        }

        return encoded.Any(char.IsWhiteSpace)
            ? new Error($"{codePrefix}.whitespace", $"{fieldName}不应包含空白字符。")
            : null;
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Encoded;
    }

    /// <inheritdoc />
    public override string ToString() => Encoded;
}

/// <summary>密码哈希。<b>领域内不出现明文密码</b>——哈希由应用层的密码哈希器产生。</summary>
public sealed class PasswordHash : SecretHash
{
    private const string ErrorCode = "identity.password_hash";

    private PasswordHash(string encoded)
        : base(encoded)
    {
    }

    /// <summary>由已编码的哈希构造。</summary>
    /// <param name="encoded">已编码的哈希。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<PasswordHash> Create(string? encoded) =>
        ValidateEncoding(encoded, ErrorCode, "密码哈希") is { } error
            ? Result.Failure<PasswordHash>(error)
            : Result.Success(new PasswordHash(encoded!));
}

/// <summary>刷新令牌哈希。<b>只保存哈希，永不保存明文令牌。</b></summary>
public sealed class TokenHash : SecretHash
{
    private const string ErrorCode = "identity.token_hash";

    private TokenHash(string encoded)
        : base(encoded)
    {
    }

    /// <summary>由已编码的哈希构造。</summary>
    /// <param name="encoded">已编码的哈希。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<TokenHash> Create(string? encoded) =>
        ValidateEncoding(encoded, ErrorCode, "令牌哈希") is { } error
            ? Result.Failure<TokenHash>(error)
            : Result.Success(new TokenHash(encoded!));
}
