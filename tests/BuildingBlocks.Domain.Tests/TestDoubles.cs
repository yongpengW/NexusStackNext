namespace NexusStackNext.BuildingBlocks.Domain.Tests;

/// <summary>强类型用户 ID。用包装类型防止"把用户 ID 当订单 ID 传"。</summary>
public sealed record UserId : StronglyTypedId<long>
{
    /// <summary>构造用户 ID。</summary>
    /// <param name="value">底层值。</param>
    public UserId(long value)
        : base(value)
    {
    }
}

/// <summary>用户名值对象：带校验，且相等性基于其组成部分。</summary>
public sealed class UserName : ValueObject
{
    /// <summary>允许的最大长度。</summary>
    public const int MaxLength = 32;

    private UserName(string value) => Value = value;

    /// <summary>规范化后的用户名。</summary>
    public string Value { get; }

    /// <summary>创建用户名，违反规则即失败。</summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回用户名，否则返回错误。</returns>
    public static Result<UserName> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<UserName>(new Error("identity.user_name.empty", "用户名不能为空。"));
        }

        var trimmed = value.Trim();
        return trimmed.Length > MaxLength
            ? Result.Failure<UserName>(new Error("identity.user_name.too_long", $"用户名不能超过 {MaxLength} 个字符。"))
            : Result.Success(new UserName(trimmed));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}

/// <summary>用户已注册。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="UserName">用户名。</param>
public sealed record UserRegistered(UserId UserId, string UserName) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>用户已被禁用。</summary>
/// <param name="UserId">用户标识。</param>
public sealed record UserDisabled(UserId UserId) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>
/// 测试用聚合根。刻意写得像真实聚合：不变量在内部强制，事件在内部记录，
/// 构造只需要调用方传参——<b>不依赖任何静态容器</b>。
/// </summary>
public sealed class User : AggregateRoot<UserId>
{
    private User(UserId id, UserName name, bool isBuiltIn)
        : base(id)
    {
        Name = name;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>用户名。</summary>
    public UserName Name { get; private set; }

    /// <summary>是否内置账号。内置账号不可禁用、不可删除。</summary>
    public bool IsBuiltIn { get; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>注册一个用户。</summary>
    /// <param name="id">由调用方提供的标识。</param>
    /// <param name="name">用户名。</param>
    /// <param name="isBuiltIn">是否内置账号。</param>
    /// <returns>新用户聚合。</returns>
    public static User Register(UserId id, UserName name, bool isBuiltIn = false)
    {
        ArgumentNullException.ThrowIfNull(name);

        var user = new User(id, name, isBuiltIn);
        user.Raise(new UserRegistered(id, name.Value));
        return user;
    }

    /// <summary>改用户名。</summary>
    /// <param name="name">新用户名。</param>
    public void Rename(UserName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>禁用账号。<b>内置账号被禁用是领域不变量违例。</b></summary>
    /// <exception cref="DomainException">试图禁用内置账号。</exception>
    public void Disable()
    {
        if (IsBuiltIn)
        {
            throw new DomainException("内置账号不可禁用。");
        }

        if (!IsEnabled)
        {
            return;
        }

        IsEnabled = false;
        Raise(new UserDisabled(Id));
    }
}
