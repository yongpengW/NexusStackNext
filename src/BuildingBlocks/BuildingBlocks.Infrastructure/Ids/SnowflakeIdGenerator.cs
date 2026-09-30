using System.Globalization;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Ids;

/// <summary>
/// 雪花算法实现：<c>(毫秒时间戳 - 纪元) &lt;&lt; 22 | WorkerId &lt;&lt; 12 | 序列号</c>。
/// <para>时钟取自已注入的 <see cref="IClock"/>，因此"现在几点"在测试里可控。</para>
/// </summary>
/// <remarks>
/// <b>两种异常情况都显式抛错，绝不静默继续：</b>
/// <list type="bullet">
///   <item><b>时钟回拨</b>——回拨时若照常递增，会生成与回拨前重复的 ID。那种错误要等到写库主键冲突
///     才暴露，而那时已经很难定位到时钟。部署环境需要 NTP 同步，这一点要写进部署文档。</item>
///   <item><b>同一毫秒内序列号耗尽</b>（4096 个）——等价于单实例超过 400 万 ID/秒，正常负载下不会发生。
///     这里刻意<b>不</b>用"借用下一毫秒"或自旋等待：借用会让内部时间跑到真实时钟前面，
///     下一次调用就会被误判成时钟回拨；自旋依赖真实时间，会让测试挂死。抛错把选择权交回调用方。</item>
/// </list>
/// </remarks>
public sealed class SnowflakeIdGenerator : IIdGenerator
{
    private const int WorkerIdBits = 10;
    private const int SequenceBits = 12;
    private const int MaxSequence = (1 << SequenceBits) - 1;
    private const int WorkerIdShift = SequenceBits;
    private const int TimestampShift = SequenceBits + WorkerIdBits;

    /// <summary>自定义纪元：2026-01-01T00:00:00Z。41 位时间戳可用约 69 年。</summary>
    private static readonly long EpochMilliseconds =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private readonly Lock _gate = new();
    private readonly int _workerId;
    private readonly IClock _clock;

    private long _lastTimestamp = -1;
    private int _sequence;

    /// <summary>构造生成器。</summary>
    /// <param name="options">配置。</param>
    /// <param name="clock">时钟。</param>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">WorkerId 超出 <c>0..1023</c>。</exception>
    public SnowflakeIdGenerator(IdGeneratorOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        if (options.WorkerId is < 0 or > IdGeneratorOptions.MaxWorkerId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.WorkerId,
                string.Create(CultureInfo.InvariantCulture, $"WorkerId 必须在 0..{IdGeneratorOptions.MaxWorkerId} 之间。"));
        }

        _workerId = options.WorkerId;
        _clock = clock;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">时钟回拨，或同一毫秒内序列号耗尽。</exception>
    public long NextId()
    {
        lock (_gate)
        {
            var timestamp = _clock.UtcNow.ToUnixTimeMilliseconds();

            if (timestamp < _lastTimestamp)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"检测到时钟回拨 {_lastTimestamp - timestamp} ms，拒绝生成 ID：继续下去会产生重复主键。请检查 NTP 同步。"));
            }

            if (timestamp == _lastTimestamp)
            {
                if (_sequence >= MaxSequence)
                {
                    throw new InvalidOperationException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"同一毫秒（{timestamp}）内已生成 {MaxSequence + 1} 个 ID，序列号耗尽。请退避后重试。"));
                }

                _sequence++;
            }
            else
            {
                _sequence = 0;
            }

            _lastTimestamp = timestamp;

            return ((timestamp - EpochMilliseconds) << TimestampShift)
                | ((long)_workerId << WorkerIdShift)
                | (long)_sequence;
        }
    }
}
