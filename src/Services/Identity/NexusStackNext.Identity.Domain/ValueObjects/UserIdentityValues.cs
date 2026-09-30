using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Domain.ValueObjects;

/// <summary>用户名。</summary>
public sealed class UserName : ValueObject
{
    /// <summary>最小长度。</summary>
    public const int MinLength = 3;

    /// <summary>最大长度。</summary>
    public const int MaxLength = 32;

    private UserName(string value) => Value = value;

    /// <summary>规范化后的用户名（已去除首尾空白）。</summary>
    public string Value { get; }

    /// <summary>构造用户名。</summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<UserName> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<UserName>(new Error("identity.user_name.empty", "用户名不能为空。"));
        }

        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
        {
            return Result.Failure<UserName>(new Error(
                "identity.user_name.length",
                $"用户名长度必须在 {MinLength}..{MaxLength} 之间。"));
        }

        return trimmed.Any(char.IsWhiteSpace)
            ? Result.Failure<UserName>(new Error("identity.user_name.whitespace", "用户名不能包含空白字符。"))
            : Result.Success(new UserName(trimmed));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>电子邮箱。</summary>
public sealed class EmailAddress : ValueObject
{
    /// <summary>最大长度（RFC 5321 对邮件地址路径的上限）。</summary>
    public const int MaxLength = 254;

    private EmailAddress(string value) => Value = value;

    /// <summary>规范化后的邮箱（已转小写）。</summary>
    public string Value { get; }

    /// <summary>
    /// 构造邮箱。
    /// <para>只做结构性校验（一个 <c>@</c>、两侧非空、域名含点、无空白），
    /// <b>不追求 RFC 完备</b>——完备的正则既难读又会误杀合法地址；真正的验证是发一封确认邮件。</para>
    /// </summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<EmailAddress> Create(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<EmailAddress>(new Error("identity.email.empty", "邮箱不能为空。"));
        }

        if (trimmed.Length > MaxLength || trimmed.Any(char.IsWhiteSpace))
        {
            return Result.Failure<EmailAddress>(new Error("identity.email.format", "邮箱格式不合法。"));
        }

        var parts = trimmed.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || !parts[1].Contains('.', StringComparison.Ordinal))
        {
            return Result.Failure<EmailAddress>(new Error("identity.email.format", "邮箱格式不合法。"));
        }

        return Result.Success(new EmailAddress(trimmed));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>手机号。</summary>
public sealed class PhoneNumber : ValueObject
{
    /// <summary>最大位数。</summary>
    public const int MaxDigits = 20;

    /// <summary>最小位数。</summary>
    public const int MinDigits = 6;

    private PhoneNumber(string value) => Value = value;

    /// <summary>规范化后的手机号。</summary>
    public string Value { get; }

    /// <summary>
    /// 构造手机号。允许一个可选的前导 <c>+</c>，其余必须是数字。
    /// 不假设国家码长度——参照仓库写死了中国大陆的规则，换个地区就废。
    /// </summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<PhoneNumber> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<PhoneNumber>(new Error("identity.phone.empty", "手机号不能为空。"));
        }

        var digits = trimmed.StartsWith('+') ? trimmed[1..] : trimmed;

        if (digits.Length < MinDigits
            || digits.Length > MaxDigits
            || !digits.All(static character => char.IsAsciiDigit(character)))
        {
            return Result.Failure<PhoneNumber>(new Error(
                "identity.phone.format",
                $"手机号必须是 {MinDigits}..{MaxDigits} 位数字，可带一个前导 +。"));
        }

        return Result.Success(new PhoneNumber(trimmed));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
