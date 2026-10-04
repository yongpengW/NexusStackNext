using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Contracts;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>本上下文内存事实副本维护的显式装配。</summary>
public static class IdentityFactCleanupServices
{
    /// <summary>在业务存储自己的写锁下维护当前已提交事实。</summary>
    /// <param name="services">容器。</param>
    /// <param name="options">维护策略。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddIdentityMemoryFactCleanup(this IServiceCollection services, CommittedFactCleanupOptions? options = null)
        => services.AddCommittedFactCleanup("identity", (provider, policy) =>
        {
            var state = provider.GetRequiredService<IdentityMemoryState>();
            return new InMemoryCommittedFactCleanup(state.Gate, () => state.Outbox, IdentityEntityCommittedV1.Name,
                policy, provider.GetRequiredService<IClock>(), state.Capacity);
        }, options);
}
