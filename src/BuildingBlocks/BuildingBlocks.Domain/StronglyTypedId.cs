namespace NexusStackNext.BuildingBlocks.Domain;

/// <summary>
/// 强类型 ID 基类。用包装类型替代裸 <c>long</c>/<c>Guid</c>，让
/// "把用户 ID 当成订单 ID 传进去"这类错误在编译期就被拦住。
/// <para>
/// <b>刻意不用位置 record 语法。</b>若基类写成 <c>record StronglyTypedId&lt;T&gt;(T Value)</c>
/// 而派生写成 <c>record UserId(long Value) : StronglyTypedId&lt;long&gt;(Value)</c>，就会出现两个同名
/// <c>Value</c> 属性互相隐藏，且派生 record 会重新生成 <c>ToString()</c> 把基类的重写覆盖掉
/// （这一条是被测试抓出来的，不是推理出来的）。因此这里让基类独占 <c>Value</c>，
/// 并把它标记为 <c>sealed override</c>，派生类型只负责提供构造函数。
/// </para>
/// <para>
/// 相等性仍然成立：record 的编译器生成 <c>Equals</c> 会沿继承链调用基类实现，
/// 而 <c>Value</c> 的字段在基类里，因此会被比较。
/// </para>
/// </summary>
/// <typeparam name="TValue">底层值类型，通常是 <see cref="long"/> 或 <see cref="Guid"/>。</typeparam>
public abstract record StronglyTypedId<TValue>
    where TValue : notnull
{
    /// <summary>构造强类型 ID。</summary>
    /// <param name="value">底层值。默认值（<c>0</c>、<c>Guid.Empty</c>）会被拒绝。</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> 为默认值时抛出。</exception>
    protected StronglyTypedId(TValue value)
    {
        if (EqualityComparer<TValue>.Default.Equals(value, default!))
        {
            throw new ArgumentException(
                $"强类型 ID 的值不能为默认值（{typeof(TValue).Name}）。", nameof(value));
        }

        Value = value;
    }

    /// <summary>底层值。</summary>
    public TValue Value { get; }

    /// <summary>只输出底层值，便于日志与 URL。</summary>
    /// <returns>底层值的字符串表示。</returns>
    public sealed override string ToString() => Value.ToString()!;
}
