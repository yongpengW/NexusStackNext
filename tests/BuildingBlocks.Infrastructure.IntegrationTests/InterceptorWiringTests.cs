using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.BuildingBlocks.Infrastructure.IntegrationTests;

/// <summary>
/// **两个 <c>SaveChanges</c> 拦截器挂在装配的缝上。**
///
/// <para>它们此前写完了却**没有任何注册点**——全仓 <c>AddInterceptors</c> 只出现在测试的探针上下文里。
/// 于是生产路径上：审计字段从不写（<c>CreatedAt</c> 一直是 default）、领域事件也不进发件箱，
/// 而"没写"与"没有要写的"从外面看是一样的。</para>
///
/// <para><b>这组测试刻意走 <c>UseNexusStackInterceptors</c>，而不是复用 <c>ProbeDatabase.OptionsFor</c>。</b>
/// 后者是**手动</b> <c>AddInterceptors</c> 的——它验的是"拦截器本身对不对"；
/// 而这里要验的是"**装配那一处**把两个都接上了"，那是两道不同的缝。
/// 少了这一条，把生产里的注册删掉不会有任何测试变红。</para>
/// </summary>
public sealed class InterceptorWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string Actor = "wiring-probe";

    /// <summary>
    /// 走生产的装配方式建上下文：<b>审计字段被写、映射得到的领域事件被入队。</b>
    /// </summary>
    [PostgresFact]
    public async Task ContextBuiltThroughTheProductionWiring_StampsAuditFields_AndQueuesMappedEvents()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var provider = BuildProvider(database, new ProbeEventMapper(new FixedClock(Now)));

        await CreateTablesAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();
            context.Probes.Add(ProbeAggregate.Create(1, "wired"));
            await context.SaveChangesAsync();
        }

        await using var verifyScope = provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ProbeDbContext>();

        var stored = await verify.Probes.SingleAsync();
        Assert.Equal(1L, stored.Id);

        // 一、**审计拦截器真的跑了**：时间与人都来自注入的时钟与当前用户，而不是 default。
        Assert.Equal(Now, stored.CreatedAt);
        Assert.Equal(Actor, stored.CreatedBy);

        // "从未修改过"是一个有意义的状态，不该被初次写入填上。
        Assert.Null(stored.UpdatedAt);
        Assert.Null(stored.UpdatedBy);

        // 二、**发件箱拦截器也真的跑了**：领域事件被映射成集成事件，并在**同一次** SaveChanges 里入队。
        var queued = await verify.Outbox.SingleAsync();
        Assert.Equal("probe.created.v1", queued.EventName);
        Assert.True(queued.IsPending);
    }

    /// <summary>
    /// 没有真映射器时：**一条都不入队**，但装配照样解得开、审计照样写。
    ///
    /// <para>它把两种状态分开了：此前"映射器缺席"会让拦截器**构造不出来**，
    /// 于是它连**审计**那一半也一起不工作——而两者本该互不牵连。
    /// 现在缺席变成了一条写下来的决定（<c>NoIntegrationEventsMapper</c>），
    /// 而发件箱为空是**事实**，不是故障：还没有任何上下文发布过集成事件。</para>
    /// </summary>
    [PostgresFact]
    public async Task WithoutARealMapper_NothingIsQueued_AndTheAuditHalfStillWorks()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var provider = BuildProvider(database, mapper: null);

        await CreateTablesAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();
            context.Probes.Add(ProbeAggregate.Create(2, "no-mapper"));
            await context.SaveChangesAsync();
        }

        await using var verifyScope = provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ProbeDbContext>();

        Assert.Equal(Now, (await verify.Probes.SingleAsync()).CreatedAt);
        Assert.Equal(0, await verify.Outbox.CountAsync());

        // 基座注册的就是那个"一律不发布"的实现——它不是空实现，是当前事实。
        Assert.IsType<NoIntegrationEventsMapper>(
            verifyScope.ServiceProvider.GetRequiredService<IIntegrationEventMapper>());
    }

    /// <summary>按生产的装配方式建容器：<c>UseNexusStackPostgres</c> + <c>UseNexusStackInterceptors</c>。</summary>
    private static ServiceProvider BuildProvider(PostgresTestDatabase database, IIntegrationEventMapper? mapper)
    {
        var services = new ServiceCollection();

        services.AddNexusStackApplication();
        services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 7 });

        // 时钟与当前用户换成确定的实现——判据要落在"注入的值"上，而不是 default。
        // 时钟注册成单例：雪花的工厂会从**根**容器解析它（Scoped 在 ValidateScopes 下会抛）。
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddScoped<ICurrentUser>(_ => new FixedCurrentUser(Actor));

        if (mapper is not null)
        {
            // 覆盖基座注册的默认映射器：最后注册的胜出。
            services.AddSingleton(mapper);
        }

        // **被测的就是这一处**：不走 ProbeDatabase.OptionsFor 的手动 AddInterceptors。
        services.AddScoped(provider =>
        {
            var builder = new DbContextOptionsBuilder<ProbeDbContext>();
            builder
                .UseNexusStackPostgres(database.ConnectionString, database.Schema)
                .UseNexusStackInterceptors(provider);

            return new ProbeDbContext(builder.Options, database.Schema);
        });

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static async Task CreateTablesAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();

        // `CreateTablesAsync` 而不是 `EnsureCreatedAsync`：理由是 ProbeDatabase 里那段注释
        // （后者在"库已存在"时什么都不做，而本仓永远共用一个库）。
        await context.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }

}
