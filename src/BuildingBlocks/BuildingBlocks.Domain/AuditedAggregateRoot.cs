namespace NexusStackNext.BuildingBlocks.Domain;

/// <summary>
/// 需要行审计的业务聚合。元数据由持久化适配器写入，不参与领域决策或版本推进。
/// 不可变操作事实使用自己的发生时间和 Actor，不继承此类型。
/// </summary>
/// <typeparam name="TId">聚合标识类型。</typeparam>
public abstract class AuditedAggregateRoot<TId> : AggregateRoot<TId>, IAuditedEntity
    where TId : notnull
{
    /// <summary>构造尚未持久化的业务聚合。</summary>
    /// <param name="id">调用方提供的标识。</param>
    protected AuditedAggregateRoot(TId id) : base(id) { }

    /// <summary>复制已观察的业务版本与审计元数据。</summary>
    /// <param name="source">已观察的聚合。</param>
    protected AuditedAggregateRoot(AuditedAggregateRoot<TId> source) : base(source)
    {
        CreatedAt = source.CreatedAt;
        CreatedBy = source.CreatedBy;
        UpdatedAt = source.UpdatedAt;
        UpdatedBy = source.UpdatedBy;
    }

    /// <summary>首次持久化时刻（UTC）；尚未持久化时为默认值。</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>首次持久化的当前操作者；系统或匿名写入为空。</summary>
    public string? CreatedBy { get; private set; }

    /// <summary>最近一次实际变更的持久化时刻（UTC）；从未修改时为空。</summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>最近一次实际变更的当前操作者；系统写入为空。</summary>
    public string? UpdatedBy { get; private set; }

    DateTimeOffset IAuditedEntity.CreatedAt { get => CreatedAt; set => CreatedAt = value; }
    string? IAuditedEntity.CreatedBy { get => CreatedBy; set => CreatedBy = value; }
    DateTimeOffset? IAuditedEntity.UpdatedAt { get => UpdatedAt; set => UpdatedAt = value; }
    string? IAuditedEntity.UpdatedBy { get => UpdatedBy; set => UpdatedBy = value; }
}
