namespace NexusStackNext.BuildingBlocks.Domain;

/// <summary>
/// 软删除契约。查询过滤器的挂载点由基础设施层统一处理，
/// 领域内不出现 "IsDeleted == false" 这类条件。
/// </summary>
public interface ISoftDelete
{
    /// <summary>是否已软删除。</summary>
    bool IsDeleted { get; }
}

/// <summary>
/// 审计字段契约。
/// <para>
/// 这些字段<b>只允许</b>由基础设施层的审计拦截器写入，领域方法不应触碰它们。
/// 之所以是 <c>set</c> 而不是只读，是因为拦截器需要跨程序集赋值；
/// 代价是"只有拦截器能写"这条约束靠约定而非编译器——这是本设计有意接受的取舍。
/// </para>
/// </summary>
public interface IAuditedEntity
{
    /// <summary>创建时间（UTC）。</summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>创建者标识。</summary>
    string? CreatedBy { get; set; }

    /// <summary>最后修改时间（UTC）；从未修改过则为 <c>null</c>。</summary>
    DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>最后修改者标识；从未修改过则为 <c>null</c>。</summary>
    string? UpdatedBy { get; set; }
}
