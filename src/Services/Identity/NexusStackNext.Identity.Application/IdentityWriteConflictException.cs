namespace NexusStackNext.Identity.Application;

/// <summary>存储适配器报告条件写入冲突；事务已回滚，不携带数据库诊断。</summary>
public sealed class IdentityWriteConflictException : InvalidOperationException
{
    /// <summary>创建安全的条件写入冲突。</summary>
    public IdentityWriteConflictException() : base("Identity 条件写入冲突。") { }
}
