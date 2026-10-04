using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using Npgsql;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>只识别所属来源的容量协议拒绝，未知存储故障保留原语义。</summary>
public static class CommittedFactCapacityFailure
{
    /// <summary>将不可重试的数据库容量标记翻译为安全原因。</summary>
    /// <param name="error">EF 保存失败。</param>
    /// <param name="owner">所属 schema。</param>
    /// <param name="exhausted">所属上下文原有的额度耗尽错误。</param>
    /// <returns>精确匹配的容量原因；其他异常返回空。</returns>
    public static Error? Read(DbUpdateException error, string owner, Error exhausted)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(exhausted);
        if (error.InnerException is not PostgresException { SqlState: "P0001" } database) { return null; }
        if (database.ConstraintName == owner + "_fact_capacity_busy") { return CommittedFactCapacityErrors.Busy; }
        return database.ConstraintName == owner + "_fact_capacity_exhausted" ? exhausted : null;
    }
}
