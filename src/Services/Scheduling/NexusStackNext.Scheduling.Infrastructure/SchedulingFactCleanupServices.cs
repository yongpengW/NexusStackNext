using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.Scheduling.Infrastructure;

/// <summary>本上下文内存事实副本维护的显式装配。</summary>
public static class SchedulingFactCleanupServices
{
    /// <summary>在业务存储自己的写锁下维护当前已提交事实。</summary>
    /// <param name="services">容器。</param>
    /// <param name="options">维护策略。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddSchedulingMemoryFactCleanup(this IServiceCollection services, CommittedFactCleanupOptions? options = null)
        => services.AddCommittedFactCleanup("scheduling", (provider, policy) =>
        {
            var state = provider.GetRequiredService<SchedulingMemoryState>();
            return new InMemoryCommittedFactCleanup(state.Writes, () => state.Outbox, PlanCommittedV1.Name,
                policy, provider.GetRequiredService<IClock>());
        }, options);
}
