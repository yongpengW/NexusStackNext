using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.BuildingBlocks.Infrastructure.IntegrationTests;

/// <summary>
/// 一条**探针聚合**：只用来验证基座的约定，不代表任何业务概念。
///
/// <para><b>为什么用探针而不是等 Identity 的实体。</b>基座的约定（主键生成方式、软删过滤器、
/// Outbox 同事务）必须先被证明，否则票据 07 会在一个没验证过的地基上盖房子——
/// 而那时出问题的症状会出现在 Identity 的测试里，离原因很远。</para>
///
/// <para>它刻意继承真实的 <see cref="AggregateRoot{TId}"/>：主键是**只读**的、
/// 由构造函数赋值，于是"应用侧生成标识"这件事在类型层面就成立，
/// 而不是靠映射配置绕过去。</para>
/// </summary>
internal sealed class ProbeAggregate : AggregateRoot<long>, ISoftDelete, IAuditedEntity
{
    private ProbeAggregate(long id, string name)
        : base(id)
    {
        Name = name;
    }

    public string Name { get; private set; }

    /// <inheritdoc />
    public bool IsDeleted { get; private set; }

    public static ProbeAggregate Create(long id, string name)
    {
        var probe = new ProbeAggregate(id, name);

        // 领域事件只带"已经发生的事实"，**不自己打时间戳**——
        // 时间由应用层的 IClock 在映射成集成事件时注入。
        probe.Raise(new ProbeCreated(Guid.NewGuid(), id));

        return probe;
    }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <inheritdoc />
    public string? UpdatedBy { get; set; }

    /// <summary>软删除。<b>不</b>调用 <c>Changed()</c>——软删改的是过滤可见性，不是聚合的可观察状态。</summary>
    public void SoftDelete() => IsDeleted = true;

    /// <summary>改个名字，用来验证"修改时写 UpdatedAt、不动 CreatedAt"。</summary>
    /// <param name="name">新名字。</param>
    public void Rename(string name) => Name = name;
}

/// <summary>探针领域事件。</summary>
/// <param name="EventId">事件标识。</param>
/// <param name="ProbeId">聚合标识。</param>
internal sealed record ProbeCreated(Guid EventId, long ProbeId) : IDomainEvent;

/// <summary>
/// 探针集成事件。事件名显式、带版本号（ADR-0007）。
///
/// <para><b>刻意不重新声明 <c>EventId</c> 与 <c>OccurredAt</c>。</b>
/// 基类已经有这两个成员，派生 record 再用位置参数声明一遍会让它们**互相隐藏**——
/// 而形状上更坏的是：<c>OccurredAt</c> 在基类里是 <c>required init</c>，
/// 重新声明之后基类那个就永远没人赋值，编译器直接拒绝。
/// 正确做法是只声明本事件特有的字段，其余用初始化器赋。</para>
/// </summary>
internal sealed record ProbeCreatedIntegration : IntegrationEvent
{
    /// <summary>聚合标识。</summary>
    public required long ProbeId { get; init; }

    /// <inheritdoc />
    public override string EventName => "probe.created.v1";
}

/// <summary>探针的领域事件 → 集成事件映射。**纯函数**，所以它自己可以脱离数据库单测。</summary>
/// <param name="clock">时钟——集成事件的时间由这里注入，不由事件自己取。</param>
internal sealed class ProbeEventMapper(IClock clock) : IIntegrationEventMapper
{
    /// <inheritdoc />
    public IntegrationEvent? Map(IDomainEvent domainEvent)
        => domainEvent is ProbeCreated created
            ? new ProbeCreatedIntegration
            {
                // 事件标识**沿用领域事件的那个**：重试时要能识别出"这是同一条消息"。
                EventId = created.EventId,
                OccurredAt = clock.UtcNow,
                ProbeId = created.ProbeId,
            }
            // 其余领域事件**不对外发布**——默认不发布是安全的那一侧。
            : null;
}

/// <summary>
/// 承载探针的上下文。schema 从构造函数传入，于是**同一个上下文能被放进任意 schema**——
/// 这既让测试能用夹具的临时 schema，也顺带证明了"schema 是可配置的"。
/// </summary>
/// <param name="options">上下文选项。</param>
/// <param name="schema">本上下文使用的 schema。</param>
internal sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options, string schema)
    : NexusStackDbContext(options, schema)
{
    public DbSet<ProbeAggregate> Probes => Set<ProbeAggregate>();

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ProbeAggregate>(entity =>
        {
            entity.ToTable("probe_aggregate");
            entity.HasKey(probe => probe.Id);
            entity.Property(probe => probe.Name).HasMaxLength(64);
        });
    }
}

/// <summary>把探针上下文接到夹具上的公共步骤。</summary>
internal static class ProbeDatabase
{
    /// <summary>按夹具的连接串与 schema 配置选项，**并装上 Outbox 拦截器**。</summary>
    /// <param name="database">测试库夹具。</param>
    /// <param name="clock">时钟；默认用固定时钟。</param>
    /// <returns>上下文选项。</returns>
    public static DbContextOptions<ProbeDbContext> OptionsFor(PostgresTestDatabase database, IClock? clock = null, ICurrentUser? currentUser = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var builder = new DbContextOptionsBuilder<ProbeDbContext>();
        builder.UseNexusStackPostgres(database.ConnectionString, database.Schema);

        builder.AddInterceptors(
            new AuditInterceptor(
                clock ?? new NexusStackNext.TestSupport.FixedClock(DateTimeOffset.UnixEpoch),
                currentUser ?? new AnonymousCurrentUser()),
            new DomainEventOutboxInterceptor(
            new ProbeEventMapper(clock ?? new NexusStackNext.TestSupport.FixedClock(DateTimeOffset.UnixEpoch)),
            new SystemTextJsonIntegrationEventSerializer()));

        return builder.Options;
    }

    /// <summary>
    /// 建好上下文并**把表建出来**。
    ///
    /// <para><b>刻意不用 <c>EnsureCreatedAsync</c>。</b>它在"数据库已存在"时**什么都不做**：
    /// 不建表、不报错、直接返回。而本仓共用一个库（ADR-0013），所以库永远存在——
    /// 于是失败出现在后面的第一次查询上（<c>42P01: relation ... does not exist</c>），离原因很远。
    /// <c>CreateTablesAsync</c> 无条件建表（DDL 里带着 <c>CREATE SCHEMA IF NOT EXISTS</c>）。</para>
    /// </summary>
    /// <param name="database">测试库夹具。</param>
    /// <param name="clock">时钟。</param>
    /// <returns>可用的上下文。</returns>
    public static async Task<ProbeDbContext> CreateAsync(PostgresTestDatabase database, IClock? clock = null, ICurrentUser? currentUser = null)
    {
        var context = new ProbeDbContext(OptionsFor(database, clock, currentUser), database.Schema);

        var creator = context.Database.GetService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync();

        return context;
    }

    /// <summary>
    /// 只开一个上下文，**不建表**——表已经由 <see cref="CreateAsync"/> 建好了。
    ///
    /// <para>需要它的场景很具体：验证"失败的写入不留痕迹"时，失败的必须是一个
    /// **全新的上下文**。在同一个上下文里插入重复主键会先在变更跟踪器里撞车
    /// （"another instance with the same key is already being tracked"），
    /// 那是客户端异常，压根到不了数据库——也就验不到事务。</para>
    /// </summary>
    /// <param name="database">测试库夹具。</param>
    /// <param name="clock">时钟。</param>
    /// <returns>可用的上下文。</returns>
    public static Task<ProbeDbContext> NewContextAsync(PostgresTestDatabase database, IClock? clock = null, ICurrentUser? currentUser = null)
        => Task.FromResult(new ProbeDbContext(OptionsFor(database, clock, currentUser), database.Schema));
}
