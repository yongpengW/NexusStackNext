using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// 所有上下文 <c>DbContext</c> 的基类。
///
/// <para><b>它只放"每个上下文都一样"的那几件事</b>：schema、主键约定、软删过滤器。
/// 凡是随上下文不同的（实体、映射、迁移）都留在各自的上下文里——
/// 这与"宿主显式组装自己"（不变量 8）是同一条道理：基类不该替上下文做决定。</para>
///
/// <para><b>schema 由构造函数传入，不是虚属性。</b>因为
/// <c>__EFMigrationsHistory</c> 的位置要在 <b>配置 <c>DbContextOptions</c> 时</b>就定下来，
/// 而那一步拿不到实例属性——它需要一个静态可知的名字。
/// 于是每个上下文写一个 <c>public const string SchemaName</c>，两处引用同一个常量。</para>
/// </summary>
/// <param name="options">上下文选项。</param>
/// <param name="schema">本上下文的 schema 名。</param>
public abstract class NexusStackDbContext(DbContextOptions options, string schema) : DbContext(options)
{
    /// <summary>本上下文在库里的 schema 名。</summary>
    public string Schema { get; } = string.IsNullOrWhiteSpace(schema)
        ? throw new ArgumentException("schema 不能为空。", nameof(schema))
        : schema;

    /// <summary>
    /// 本上下文的待投递消息表。
    ///
    /// <para><b>它由基座提供，不由上下文自己声明。</b>Outbox 与聚合写入必须**同一个事务**，
    /// 而"同一个事务"的前提是**同一个 <c>DbContext</c>**——把它留给各上下文自行添加，
    /// 就会出现"某个上下文的聚合在自己的库里、Outbox 在另一个连接上"这种情况，
    /// 而那正是这条保证失效的方式。基座把表放进每个上下文自己的 schema，同事务就成立。</para>
    /// </summary>
    public DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();

    /// <summary>本上下文的事务性收件箱表。理由同 <see cref="Outbox"/>。</summary>
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    /// <summary>
    /// **基类接管建模范式，派生类只提供自己的实体。**
    ///
    /// <para><b>为什么 <c>sealed</c>。</b>约定必须**在派生类配置完实体之后**施加，
    /// 否则它跑的时候模型里一个实体都还没有——第一版就是这么错的：
    /// 派生类在 <c>base.OnModelCreating</c> <b>之后</b>才调用 <c>Entity&lt;T&gt;()</c>，
    /// 于是 <c>ValueGeneratedNever</c> 一条都没生效，主键列照样长出了 IDENTITY。
    /// 编译器不会报错，只有把建表 DDL 打出来看才会发现。</para>
    ///
    /// <para>与其在文档里要求"记得最后调用约定"，不如让顺序不可能写错：
    /// 基类独占 <c>OnModelCreating</c>，派生类实现 <see cref="ConfigureModel"/>，
    /// 约定的位置由基类保证。</para>
    /// </summary>
    /// <param name="modelBuilder">模型构建器。</param>
    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        // 基座自己的两张表：先配，派生类改不动它们（约定最后还会再压一层）。
        ConfigureOutboxAndInbox(modelBuilder);

        ConfigureModel(modelBuilder);

        // **最后**施加：约定是底线的底线，覆盖派生类可能写出的任何配置。
        modelBuilder.ApplyNexusStackConventions();
    }

    /// <summary>Outbox 与 Inbox 的映射。由基座统一提供，见 <see cref="Outbox"/> 的说明。</summary>
    private static void ConfigureOutboxAndInbox(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxEntry>(entity =>
        {
            entity.ToTable("outbox");
            entity.HasKey(entry => entry.Id);

            // 计算属性——不是列。漏掉 Ignore 的话 EF 会试着为它们建列，
            // 而它们是只读的，于是报"没有可映射的 setter"，离原因很远。
            entity.Ignore(entry => entry.IsDelivered);
            entity.Ignore(entry => entry.IsDeadLettered);
            entity.Ignore(entry => entry.IsPending);

            entity.Property(entry => entry.EventName).HasMaxLength(200).IsRequired();
            entity.Property(entry => entry.Payload).IsRequired();
            entity.Property(entry => entry.LastFailure).HasMaxLength(2000);

            // 投递循环的查询形状就是这三个条件。**索引与查询成对出现**，
            // 否则它只是装饰——而这个查询每几秒就要跑一次。
            entity.HasIndex(entry => new { entry.DeliveredAt, entry.DeadLetteredAt, entry.NextAttemptAt })
                .HasDatabaseName("ix_outbox_pending");
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox");

            // 主键即去重键——不需要第二处唯一索引去表达同一件事。
            entity.HasKey(message => new { message.ConsumerName, message.EventName, message.MessageId });

            entity.Property(message => message.ConsumerName).HasMaxLength(200).IsRequired();
            entity.Property(message => message.EventName).HasMaxLength(200).IsRequired();
        });
    }

    /// <summary>派生类在这里配置自己的实体。它在全局约定**之前**执行。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    protected abstract void ConfigureModel(ModelBuilder modelBuilder);
}

/// <summary>把本仓的建模范式固化成 EF 约定。</summary>
public static class ModelBuilderConventions
{
    /// <summary>
    /// 应用三条全局约定：**主键永不由数据库生成**、**软删实体自动过滤**、schema 已在基类设定。
    ///
    /// <para><b>为什么主键必须是 <c>ValueGeneratedNever</c>。</b>
    /// 标识由应用侧生成（雪花 ID，见 <c>IIdGenerator</c>），因此数据库列**不能**是
    /// <c>IDENTITY</c>。参照仓库恰好是这一条的两个权威并存：实体上标了
    /// <c>ValueGeneratedNever</c>，而迁移生成的列却带着自增，序列停在 1，
    /// 与种子 admin 撞车（ADR-0009）。让约定在**基类**里统一施加，
    /// 就不会出现"某个上下文漏标了一个实体"这第三种情况。</para>
    ///
    /// <para><b>为什么软删过滤器要在这里挂。</b><see cref="ISoftDelete"/> 的文档写着
    /// "查询过滤器的挂载点由基础设施层统一处理，领域内不出现 <c>IsDeleted == false</c> 这类条件"。
    /// 统一在这里挂，领域里就真的不用写；漏挂的后果是软删记录**照常出现在查询里**，
    /// 而那是一个不会报错的错误。</para>
    ///
    /// <para><b>注意过滤器是对每个实体单独挂的。</b>EF Core 不支持"给所有实现某接口的类型挂一个过滤器"，
    /// 所以这里逐个来——加了新实体忘了软删过滤器时，本方法覆盖不到它，
    /// 这一点由 <c>SoftDeleteConventionTests</c> 的反向验证盯着（故意漏挂一个 → 测试红）。</para>
    /// </summary>
    /// <param name="modelBuilder">模型构建器。</param>
    public static void ApplyNexusStackConventions(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            ApplyKeyConvention(entityType);
            ApplySoftDeleteFilter(modelBuilder, entityType);
        }
    }

    private static void ApplyKeyConvention(IMutableEntityType entityType)
    {
        var primaryKey = entityType.FindPrimaryKey();

        if (primaryKey is null)
        {
            return;
        }

        foreach (var property in primaryKey.Properties)
        {
            // 覆盖显式配置——这也是有意的：这条是**全仓约定**，
            // 不该被某个上下文的一次疏忽悄悄关掉。
            property.ValueGenerated = ValueGenerated.Never;
        }
    }

    private static void ApplySoftDeleteFilter(ModelBuilder modelBuilder, IMutableEntityType entityType)
    {
        if (!typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType))
        {
            return;
        }

        // 已经是根实体才挂：EF 对派生类型有自己的过滤器继承规则，重复挂会抛。
        if (entityType.BaseType is not null)
        {
            return;
        }

        var parameter = System.Linq.Expressions.Expression.Parameter(entityType.ClrType, "entity");

        var body = System.Linq.Expressions.Expression.Equal(
            System.Linq.Expressions.Expression.Property(parameter, nameof(ISoftDelete.IsDeleted)),
            System.Linq.Expressions.Expression.Constant(false));

        modelBuilder.Entity(entityType.ClrType).HasQueryFilter(
            System.Linq.Expressions.Expression.Lambda(body, parameter));
    }
}
