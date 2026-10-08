using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>
/// 创建用户请求的校验。
///
/// <para><b>它挡的是一件领域挡不住的事。</b>域里的 <c>PasswordHash</c> 只校验
/// "看起来像不像一个已编码的哈希"——它**从来看不到明文**，因为明文在进领域之前
/// 就被换成了哈希。于是：一个空口令会被哈希、被接受、被存起来，
/// 而整条链路上没有任何一环觉得不对。</para>
///
/// <para><b>所以分界线是"形状 vs 不变量"</b>：口令策略（长度、字符构成）是**输入的形状**，
/// 归校验器；而"口令哈希必须像哈希"是**领域不变量**，归聚合。
/// 把口令策略塞进领域是做不到的（领域拿不到明文），把它留给调用方则是没人负责。</para>
///
/// <para>分发器在**开启事务之前**调用校验器（见 <c>Sender</c>）——
/// 一条不合法的请求不该占用一个数据库连接。</para>
/// </summary>
public sealed class CreateUserCommandValidator : IRequestValidator<CreateUserCommand>
{
    /// <summary>口令最短长度。</summary>
    public const int MinimumPasswordLength = 12;

    /// <inheritdoc />
    public Result Validate(CreateUserCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UserPasswordPolicy.Validate(request.Password);
    }
}

internal static class UserPasswordPolicy
{
    internal static Result Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return Result.Failure(new Error("identity.password.empty", "口令不能为空。"));
        }

        // 长度而不是"复杂度规则"。复杂度规则（大小写+数字+符号）会把用户推向
        // `Passw0rd!` 这类可预测的形状，而长度是唯一一条被反复验证有效的约束。
        return password.Length < CreateUserCommandValidator.MinimumPasswordLength
            ? Result.Failure(new Error(
                "identity.password.too_short",
                $"口令长度不得少于 {CreateUserCommandValidator.MinimumPasswordLength} 个字符。"))
            : Result.Success();
    }
}
