using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>把本仓的 PostgreSQL 约定接到 <see cref="DbContextOptionsBuilder"/> 上。</summary>
public static class NexusStackDbContextOptionsExtensions
{
    /// <summary>
    /// 本仓的迁移历史表名。**在<b>每个上下文的 schema</b> 里各存一份**，不是全库一份。
    /// </summary>
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    /// <summary>
    /// 按本仓约定配置 PostgreSQL。
    ///
    /// <para><b>为什么必须显式指定迁移历史表的位置。</b>
    /// <c>HasDefaultSchema</c> **不会**把 <c>__EFMigrationsHistory</c> 一起 schema 化——
    /// 它落在<b>连接串的默认 schema</b>（通常是 <c>public</c>）里。
    /// 这一点是实测出来的，不是推理：本仓做过一次 DDL 核对，发现五个上下文
    /// 会在 <c>public</c> 里共用同一张历史表，于是"每个上下文各自迁移"这件事
    /// 从结构上就不可能——第二家跑迁移时会看到第一家已经建过的表。
    /// 显式传 <paramref name="schema"/> 之后，一个库五个 schema 与五个库两种部署
    /// <b>只靠换连接串就能切换</b>，不必改代码。</para>
    ///
    /// <para><b>为什么打开重试。</b><c>EnableRetryOnFailure</c> 会装上执行策略，
    /// 而执行策略与"用户自建事务"不能直接共存——这正是
    /// <see cref="Application.Transactions.IUnitOfWork"/> 文档里那条约束的来源。
    /// 本仓的选择是<b>保留执行策略，由它包裹事务</b>，实现见 <c>EfUnitOfWork</c>。</para>
    /// </summary>
    /// <param name="builder">选项构建器。</param>
    /// <param name="connectionString">连接串。</param>
    /// <param name="schema">本上下文的 schema 名。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static DbContextOptionsBuilder UseNexusStackPostgres(
        this DbContextOptionsBuilder builder,
        string connectionString,
        string schema)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable(MigrationsHistoryTableName, schema);

            // 瞬时故障（连接抖动、死锁牺牲者）自动重试。
            npgsql.EnableRetryOnFailure();
        });

        // **schema 必须参与模型缓存键。**
        //
        // EF 默认按 `DbContext` **类型**缓存模型——`HasDefaultSchema` 是烘进模型里的，
        // 于是"同一个上下文类型配了不同 schema"时，第二个实例会拿到第一个建好的模型，
        // 表和查询落到**错误的 schema** 里。
        //
        // 这个坑在本票的集成测试里实测踩到：四个用例各自用独立的临时 schema，
        // 单独跑全绿，**一起跑就失败**（`relation ... does not exist`），
        // 因为只有第一个用例的 schema 真正进了模型。
        //
        // 生产里每个上下文的 schema 是常量，等价于原来的行为、没有额外开销；
        // 但让它**正确**比让它"恰好在这个用法下正确"更值——schema 是本基类的构造参数，
        // 那就得按"它可以变"来处理。
        builder.ReplaceService<IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>();

        return builder;
    }

    /// <summary>
    /// 把本仓的两个 <c>SaveChanges</c> 拦截器接到这个上下文上：<b>审计字段</b>与<b>发件箱</b>。
    ///
    /// <para><b>为什么这是一个扩展方法，而不是在各处 <c>AddInterceptors</c>。</b>
    /// 这两个拦截器此前**写完了但没有任何注册点**——全仓 <c>AddInterceptors</c> 只出现在测试的探针上下文里。
    /// 后果是安静的：审计字段在生产里从不写（<c>CreatedAt</c> 一直是 default），
    /// 领域事件也不进发件箱。而"没写"与"没有要写的"从外面看是一样的。</para>
    ///
    /// <para>把它们接在**装配的缝**上（用了 <c>UseNexusStackPostgres</c> 的上下文都会调这里），
    /// 任何新增的 EF 上下文都不必记得这件事——与"每个宿主显式组装"同一条道理：
    /// 组装写在一处，读代码的人看得见它由什么组成。</para>
    /// </summary>
    /// <param name="builder">选项构建器。</param>
    /// <param name="services">服务提供者（拦截器从它取时钟、当前用户、映射器与序列化器）。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static DbContextOptionsBuilder UseNexusStackInterceptors(
        this DbContextOptionsBuilder builder,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);

        // `ICurrentUser` 用 GetService 而不是 GetRequiredService：**基座不注册它**
        // （`AddNexusStackApplication` 只管应用层基座），它由宿主按自己的认证形态注册。
        // 没有它时用 `AnonymousCurrentUser`——那是"认证还没接入"的正确表现，
        // 而不是一次失败：此时 `CreatedBy` 本来就该是 null。
        // （这条是实测出来的：改成 GetRequiredService 会让 Identity 的 16 条集成测试一起红。）
        var currentUser = services.GetService<ICurrentUser>() ?? new AnonymousCurrentUser();

        builder.AddInterceptors(
            new AuditInterceptor(services.GetRequiredService<IClock>(), currentUser),
            new DomainEventOutboxInterceptor(
                services.GetRequiredService<IIntegrationEventMapper>(),
                services.GetRequiredService<IIntegrationEventSerializer>()));

        return builder;
    }

    /// <summary>泛型重载，便于 <c>AddDbContext&lt;TContext&gt;</c> 里直接使用。</summary>
    /// <typeparam name="TContext">上下文类型。</typeparam>
    /// <param name="builder">选项构建器。</param>
    /// <param name="connectionString">连接串。</param>
    /// <param name="schema">本上下文的 schema 名。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static DbContextOptionsBuilder<TContext> UseNexusStackPostgres<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString,
        string schema)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        UseNexusStackPostgres((DbContextOptionsBuilder)builder, connectionString, schema);
        return builder;
    }
}
