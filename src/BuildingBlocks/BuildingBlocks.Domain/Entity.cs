namespace NexusStackNext.BuildingBlocks.Domain;

/// <summary>
/// 实体标记接口。故意不暴露 <c>Id</c>——泛型 ID 装箱成 <see cref="object"/> 会带来无谓的分配，
/// 需要 ID 的通用组件应当对具体类型泛型化。
/// </summary>
public interface IEntity;

/// <summary>
/// 实体基类。
/// <para>
/// <b>ID 由调用方传入，绝不在构造函数里生成。</b>
/// 参照仓库的反例是构造函数直接调 <c>SnowFlake.Instance</c>，依赖一个启动后才初始化的静态容器，
/// 导致 <c>new User()</c> 在任何单元测试里都会抛异常——整个项目从结构上无法做单元测试。
/// 见 <c>AGENTS.md</c> 不变量 6。
/// </para>
/// </summary>
/// <typeparam name="TId">标识类型，通常是强类型 ID。</typeparam>
public abstract class Entity<TId> : IEntity
    where TId : notnull
{
    /// <summary>构造实体。</summary>
    /// <param name="id">由调用方提供的标识。默认值（<c>0</c>、<c>Guid.Empty</c>）会被拒绝。</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> 为默认值时抛出。</exception>
    protected Entity(TId id)
    {
        if (EqualityComparer<TId>.Default.Equals(id, default!))
        {
            throw new ArgumentException($"实体 ID 不能为默认值（{typeof(TId).Name}）。", nameof(id));
        }

        Id = id;
    }

    /// <summary>实体标识。构造后不可变。</summary>
    public TId Id { get; }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is Entity<TId> other
        && GetType() == other.GetType()
        && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    /// <inheritdoc />
    public override string ToString() => $"{GetType().Name}({Id})";
}
