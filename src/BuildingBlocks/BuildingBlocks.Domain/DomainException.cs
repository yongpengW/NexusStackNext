namespace NexusStackNext.BuildingBlocks.Domain;

/// <summary>
/// 领域不变量被违反时抛出。
/// <para>
/// 只用于"不可能发生却被请求了"的情况——例如删除内置角色。
/// <b>预期内的业务失败请用 <see cref="Result"/></b>，不要用异常。
/// </para>
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>以说明构造。</summary>
    /// <param name="message">面向人的说明。</param>
    public DomainException(string message)
        : base(message)
    {
    }

    /// <summary>以说明与内层异常构造。</summary>
    /// <param name="message">面向人的说明。</param>
    /// <param name="innerException">内层异常。</param>
    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以默认说明构造。</summary>
    public DomainException()
        : base("领域不变量被违反。")
    {
    }
}
