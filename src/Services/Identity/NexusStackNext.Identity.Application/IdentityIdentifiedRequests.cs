using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>
/// 请求里带着**来自外部的实体标识**。
///
/// <para><b>为什么需要这个标记。</b>强类型 ID 的构造函数会拒绝默认值——
/// <c>new UserId(0)</c> 抛 <c>ArgumentException</c>，那是**领域在保护自己**。
/// 但标识常常直接来自 URL（<c>/users/{userId}/permissions</c>），
/// 于是"用户填了个 0"就会变成一次**服务端异常**：500。</para>
///
/// <para>用户输入不该让服务端抛异常。这个标记让一个校验器覆盖所有带标识的请求——
/// 靠的是 <see cref="IRequestValidator{TRequest}"/> 的类型参数**逆变**，
/// 那正是它的文档里写明要解决的问题。</para>
/// </summary>
public interface IIdentifiedRequest
{
    /// <summary>本请求携带的全部实体标识。</summary>
    IReadOnlyList<long> Identifiers { get; }
}

/// <summary>
/// 标识必须是正数。
///
/// <para><b>它是 400 与 500 的分界线。</b>没有它时，<c>/users/0/permissions</c>
/// 会走到 <c>new UserId(0)</c> 并抛出——而调用方收到的是一条
/// "服务器内部错误"，那既不说明哪里错了，也不说明该怎么改。
/// 有了它，同一个请求得到的是 <c>400</c> 与一句"标识必须是正数"。</para>
///
/// <para>它注册一次就覆盖了所有实现 <see cref="IIdentifiedRequest"/> 的请求——
/// 这就是把校验放在**形状**上而不是逐个请求上的好处。</para>
/// </summary>
public sealed class IdentifiedRequestValidator : IRequestValidator<IIdentifiedRequest>
{
    /// <inheritdoc />
    public Result Validate(IIdentifiedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var identifier in request.Identifiers)
        {
            if (identifier <= 0)
            {
                return Result.Failure(new Error(
                    "identity.identifier.invalid",
                    $"标识必须是正数，收到的是 {identifier}。"));
            }
        }

        return Result.Success();
    }
}
