namespace NexusStackNext.BuildingBlocks.Domain;

/// <summary>
/// 值对象基类：没有标识，只有属性；两个值对象相等当且仅当所有组成部分相等。
/// <para>
/// 派生类只需实现 <see cref="GetEqualityComponents"/>，<b>不要</b>手写
/// <c>Equals</c>/<c>GetHashCode</c>——少写一处就少一处不一致的机会。
/// </para>
/// </summary>
public abstract class ValueObject
{
    /// <summary>参与相等性比较的组成部分，顺序必须稳定。</summary>
    /// <returns>组成部分序列。</returns>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType())
        {
            return false;
        }

        return obj is ValueObject other
            && GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    /// <summary>相等比较。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否相等。</returns>
    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>不等比较。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否不等。</returns>
    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
