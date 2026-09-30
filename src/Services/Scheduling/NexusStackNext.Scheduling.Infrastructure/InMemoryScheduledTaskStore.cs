using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Infrastructure;

/// <summary>
/// 计划任务的内存存储。
/// <para>
/// <b>进程重启即丢失。</b>它是开发与测试用的实现——EF Core 版接入时应当**新增**而不是替换，
/// 因为调度器的一轮逻辑需要一个能被确定性构造的存储（见 <c>ScheduleRunner</c> 的测试）。
/// </para>
/// <para>
/// 本类原先住在 <c>Scheduling.Api</c> 里。适配器住在宿主里是分层走样：
/// 换一个持久化实现要动宿主，而宿主本该只负责组装。票据 40 把它挪到了这里。
/// </para>
/// </summary>
public sealed class InMemoryScheduledTaskStore : IScheduledTaskStore
{
    private readonly ConcurrentDictionary<long, ScheduledTask> _tasks = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ReadDueAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ScheduledTask> due =
        [
            .. _tasks.Values
                .Where(task => task.IsDue(now))
                .OrderBy(static task => task.Code.Value, StringComparer.Ordinal)
                .Take(batchSize),
        ];

        return Task.FromResult(due);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ScheduledTask> all =
        [
            .. _tasks.Values.OrderBy(static task => task.Code.Value, StringComparer.Ordinal),
        ];

        return Task.FromResult(all);
    }

    /// <inheritdoc />
    public Task SaveAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        _tasks[task.Id.Value] = task;
        return Task.CompletedTask;
    }
}

/// <summary>把 Scheduling 的端口接到内存适配器上。</summary>
public static class SchedulingInfrastructureServiceCollectionExtensions
{
    /// <summary>注册内存任务存储、单轮执行器与注册表。<b>显式注册，不做程序集扫描</b>（架构不变量 8）。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddSchedulingInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IScheduledTaskStore, InMemoryScheduledTaskStore>();

        // **必须是 Singleton。** SchedulingWorker 是托管服务（单例），
        // 单例不能消费 Scoped——把它注册成 Scoped 会在宿主启动时直接抛，
        // 而那时错误信息指向的是 DI 而不是这里的意图。
        services.AddSingleton<ScheduleRunner>();
        services.AddScoped<TaskRegistry>();

        return services;
    }
}
