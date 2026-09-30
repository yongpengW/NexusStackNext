using System.Diagnostics.CodeAnalysis;

namespace NexusStackNext.BuildingBlocks.Domain;

/// <summary>
/// 业务失败。<b>用返回值表达，而不是抛异常</b>——参照仓库用异常表达业务失败，
/// 又在外层把所有响应统一成 HTTP 200，调用方无法区分"成功"与"失败"（review/03）。
/// 异常只用于真正的意外，不用于"余额不足"这类预期内的分支。
/// </summary>
/// <param name="Code">稳定的机器可读错误码，例如 <c>identity.user.not_found</c>。</param>
/// <param name="Message">面向人的说明。</param>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Error 是 Result 风格 API 的核心词汇（Result.Failure(Error)），本库只面向 C#。"
        + "改成 DomainError 会让每一次调用与每一条断言都变长，收益不抵成本。")]
public sealed record Error(string Code, string Message)
{
    /// <summary>表示"没有错误"。仅用于 <see cref="Result"/> 的成功路径。</summary>
    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>空错误码表示成功。</summary>
    public bool IsNone => string.IsNullOrEmpty(Code);

    /// <summary>便于记录的字符串表示。</summary>
    public override string ToString() => IsNone ? "OK" : $"{Code}: {Message}";
}

/// <summary>无返回值的操作结果。</summary>
public class Result
{
    /// <summary>构造结果。</summary>
    /// <param name="isSuccess">是否成功。</param>
    /// <param name="error">失败原因；成功时必须是 <see cref="Error.None"/>。</param>
    /// <exception cref="InvalidOperationException">成功与错误参数自相矛盾时抛出。</exception>
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (isSuccess && !error.IsNone)
        {
            throw new InvalidOperationException("成功的结果不能携带错误。");
        }

        if (!isSuccess && error.IsNone)
        {
            throw new InvalidOperationException("失败的结果必须携带错误。");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>是否失败。</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>失败原因；成功时为 <see cref="Error.None"/>。</summary>
    public Error Error { get; }

    /// <summary>成功（无返回值）。</summary>
    /// <returns>成功结果。</returns>
    public static Result Success() => new(true, Error.None);

    /// <summary>失败（无返回值）。</summary>
    /// <param name="error">失败原因。</param>
    /// <returns>失败结果。</returns>
    public static Result Failure(Error error) => new(false, error);

    /// <summary>成功（带值）。</summary>
    /// <typeparam name="TValue">值类型。</typeparam>
    /// <param name="value">返回值。</param>
    /// <returns>成功结果。</returns>
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    /// <summary>失败（带值类型）。</summary>
    /// <typeparam name="TValue">值类型。</typeparam>
    /// <param name="error">失败原因。</param>
    /// <returns>失败结果。</returns>
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

/// <summary>带返回值的操作结果。</summary>
/// <typeparam name="TValue">值类型。</typeparam>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error) => _value = value;

    /// <summary>
    /// 成功时的返回值。访问失败结果的值会抛异常——
    /// 这与"不把失败当正常流程"的取向一致：先判 <see cref="Result.IsSuccess"/>。
    /// </summary>
    /// <exception cref="InvalidOperationException">结果失败时访问。</exception>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("失败的结果没有值。");

    /// <summary>便于在日志里安全地取值。</summary>
    public TValue? ValueOrDefault => _value;
}
