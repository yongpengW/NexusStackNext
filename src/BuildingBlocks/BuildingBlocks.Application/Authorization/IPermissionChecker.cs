using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Authorization;

/// <summary>
/// 读出某个用户当前的有效权限。
///
/// <para><b>为什么它是一个端口而不是直接依赖 Identity 的缓存。</b>授权过滤器要被
/// **五个上下文**共用，而权限缓存属于 Identity。端口让过滤器只认识"能问出权限"这件事，
/// 由 Identity 提供实现（票据 08 的预计算缓存）。</para>
///
/// <para><b>失败就是拒绝。</b>返回 <see cref="Result"/> 而不是"空集合"是有意的：
/// 空集合表示"这个人确实没有权限"，而失败表示"我问不出来"。
/// 两者在授权上的结论相同（都拒绝），但在**诊断**上完全不同——
/// 前者要去看角色配置，后者要去看数据库。</para>
/// </summary>
public interface IPermissionChecker
{
    /// <summary>读出某个用户的有效权限。</summary>
    /// <param name="userId">用户标识（字符串形式，因为调用方来自声明，不认识领域类型）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>权限键集合；查不出来时是失败。</returns>
    Task<Result<PermissionKeySet>> ReadAsync(string userId, CancellationToken cancellationToken = default);
}
