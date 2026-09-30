using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>
/// 内存审计条目存储。
/// <para>
/// <b>追加只写</b>：接口上没有修改与删除，聚合上也没有 setter——
/// "发生过的事不可被改写"如果只靠约定，迟早会有人加一个 <c>Update</c>。
/// </para>
/// <para>
/// 与其它内存适配器一样，它是开发与测试用的：进程重启即丢失。
/// 真实的审计存储必须是追加写、可校验（例如带哈希链）的持久化实现。
/// </para>
/// </summary>
public sealed class InMemoryAuditEntryStore : IAuditEntryStore
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    /// <summary>当前条目数。<b>只给测试与诊断用</b>——业务侧没有查询入口（ADR-0001）。</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public Task AddAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}

/// <summary>把 Auditing 的端口接到内存适配器上。</summary>
public static class AuditingInfrastructureServiceCollectionExtensions
{
    /// <summary>注册内存审计存储、内存收件箱与摄取服务。<b>显式注册，不做程序集扫描</b>（架构不变量 8）。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddAuditingInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuditEntryStore, InMemoryAuditEntryStore>();
        services.AddSingleton<IInboxStore, InMemoryInboxStore>();
        services.AddScoped<AuditIngestion>();

        return services;
    }
}
