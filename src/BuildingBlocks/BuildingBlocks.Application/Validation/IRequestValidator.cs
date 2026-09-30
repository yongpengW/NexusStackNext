using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Validation;

/// <summary>
/// 请求校验器。由分发器在**开启事务之前**调用。
/// <para>
/// <b>只是标记接口</b>，不认识任何请求类型——分发器按请求的运行时类型构造
/// <c>IRequestValidator&lt;TRequest&gt;</c> 后经由包装器强类型调用，因此这里不需要暴露
/// <c>object</c> 形状的方法。
/// </para>
/// <para>
/// 没有引入第三方校验库：本层只需要"返回一个 <see cref="Result"/>"，多一个依赖不划算。
/// 需要复杂规则时，规则本身属于领域，应当放进聚合而不是校验器。
/// </para>
/// <para>类型参数声明为逆变（<c>in</c>），因此可以为基类型注册一个校验器覆盖多个请求。</para>
/// </summary>
public interface IRequestValidator;

/// <summary>校验某个请求类型。</summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
public interface IRequestValidator<in TRequest> : IRequestValidator
{
    /// <summary>校验请求。</summary>
    /// <param name="request">请求。</param>
    /// <returns>成功表示通过。</returns>
    Result Validate(TRequest request);
}
