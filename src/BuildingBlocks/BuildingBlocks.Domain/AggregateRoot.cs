namespace NexusStackNext.BuildingBlocks.Domain;

/// <summary>
/// 领域事件：发生在领域内部、已经发生的事实。
/// <para>
/// 只携带 <see cref="EventId"/>，<b>不自己打时间戳</b>——时间必须由应用层的 <c>IClock</c> 注入，
/// 否则事件的发生时刻无法在测试中确定（参照仓库的 <c>CronScheduleService</c> 正是栽在静态时间上）。
/// </para>
/// <para>
/// 领域事件与"集成事件"不是一回事：领域事件是进程内的，集成事件是跨上下文、契约化、带版本号的
/// （见 ADR-0007）。二者之间的转换发生在应用层。
/// </para>
/// </summary>
public interface IDomainEvent
{
    /// <summary>事件实例标识，用于去重与追踪。</summary>
    Guid EventId { get; }
}

/// <summary>持有待发布领域事件的聚合根。</summary>
public interface IHasDomainEvents
{
    /// <summary>尚未发布的领域事件。</summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>清空待发布事件。由 Outbox 抽取后调用。</summary>
    void ClearDomainEvents();
}

/// <summary>
/// 聚合根：一致性边界的根。<b>一个聚合 = 一个事务</b>（<c>AGENTS.md</c> 不变量 4）。
/// 跨聚合与跨上下文的一致性只能是最终一致的，经 Outbox 达成。
/// </summary>
/// <typeparam name="TId">标识类型。</typeparam>
public abstract class AggregateRoot<TId> : Entity<TId>, IHasDomainEvents
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>构造聚合根。</summary>
    /// <param name="id">由调用方提供的标识。</param>
    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    /// <summary>
    /// 乐观并发版本号。<b>新建聚合是 1，状态每改变一次 +1。</b>
    /// <para>
    /// 它<b>不是给数据库看的</b>——它是领域的乐观并发依据。理由是"这个状态是第几版"
    /// 本身是业务事实，而不该取决于用了哪个 ORM；换掉 EF 不应该顺带失去并发保护。
    /// 见 <c>docs/adr/0011-optimistic-concurrency-in-the-aggregate.md</c>。
    /// </para>
    /// <para>
    /// <b>契约：Version 改变，当且仅当可观察状态改变了。</b>
    /// 空操作若自增，乐观并发会在没有冲突的情况下误报冲突——而误报的代价是
    /// 调用方开始重试或干脆忽略冲突，那时这个机制就废了。
    /// </para>
    /// <para>只读与不可变聚合（<c>ApiResource</c>、<c>AuditEntry</c>）永远停在 1。</para>
    /// </summary>
    public long Version { get; private set; } = 1;

    /// <inheritdoc />
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <inheritdoc />
    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// 记录一个领域事件。只能由聚合自身调用——这正是"不变量留在领域内"的体现：
    /// 外部无法替聚合宣布它发生了什么。
    /// </summary>
    /// <param name="domainEvent">已发生的事实。</param>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// 声明"这一步改变了状态"，并返回成功。
    /// <para>
    /// 存在的意义是让改变路径**一眼可辨**：方法末尾写 <c>return Changed();</c> 而不是
    /// <c>return Result.Success();</c>，读代码的人就知道这里动了状态、版本会 +1。
    /// 而提前返回的 <c>Result.Success()</c>（空操作路径）则明确表示没有改变。
    /// </para>
    /// </summary>
    /// <returns>成功。</returns>
    protected Result Changed()
    {
        Version++;
        return Result.Success();
    }

    /// <summary>声明"这一步改变了状态"，并返回带值的成功。</summary>
    /// <typeparam name="TValue">返回值类型。</typeparam>
    /// <param name="value">返回值。</param>
    /// <returns>成功。</returns>
    protected Result<TValue> Changed<TValue>(TValue value)
    {
        Version++;
        return Result.Success(value);
    }

    /// <summary>
    /// 声明"这一步改变了状态"，用于返回 <c>void</c> 的路径。
    /// <para>与 <see cref="Changed()"/> 是同一件事，只是没有返回值可带。</para>
    /// </summary>
    protected void BumpVersion() => Version++;
}
