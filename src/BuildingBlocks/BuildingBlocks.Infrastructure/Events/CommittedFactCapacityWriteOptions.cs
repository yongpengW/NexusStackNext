using System.Globalization;
using Npgsql;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>PostgreSQL 事实触发器获取容量锁的等待预算；不改变普通业务命令预算。</summary>
public sealed record CommittedFactCapacityWriteOptions
{
    /// <summary>每次容量锁获取的上限，默认三秒；支持五十毫秒至三十秒的整毫秒值。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>在宿主装配时拒绝无界或无效配置。</summary>
    public void Validate()
    {
        if (Timeout < TimeSpan.FromMilliseconds(50) || Timeout > TimeSpan.FromSeconds(30)
            || Timeout.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new InvalidOperationException("事实容量锁等待预算必须是五十毫秒至三十秒的整毫秒值。");
        }
    }

    /// <summary>将经过验证的私有参数带入所属连接；触发器才局部应用锁等待设置。</summary>
    /// <param name="connectionString">所属上下文已有连接配置。</param>
    /// <returns>包含本上下文等待预算的连接配置；不得记录或输出。</returns>
    public string ConfigureConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Validate();
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var milliseconds = ((int)Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        connection.Options = $"{connection.Options} -c nsn.fact_capacity_wait_ms={milliseconds}".Trim();
        return connection.ConnectionString;
    }
}
