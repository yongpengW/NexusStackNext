using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Application;

/// <summary>
/// 计划任务的注册表：定义、列出、停用、启用。
///
/// <para><b>它挡在存储前面，做三件存储不该管的事</b>：编码唯一性、标识生成、首次执行时刻的默认值。
/// 端口因此只需要三个方法，而不是把"查重"和"按名找"也变成存储的职责。</para>
///
/// <para><b>停用会清空下次计划时刻</b>（聚合保证）。这一条很容易写错：
/// 只把 <c>IsEnabled</c> 置 false 而留着 <c>NextRunAt</c>，
/// 任务在 <c>ReadDueAsync</c> 眼里仍然是"到期"的——于是它每轮都被读出来、每轮都被跳过，
/// 日志上看起来一切正常，实际上调度器在空转。</para>
///
/// <para>接口四个方法，后面是：唯一性、默认时刻、两个状态转换、以及一条"任务不存在"的错误路径。</para>
/// </summary>
/// <param name="store">任务存储。</param>
/// <param name="ids">标识生成器。</param>
/// <param name="clock">时钟。</param>
public sealed class TaskRegistry(IScheduledTaskStore store, IIdGenerator ids, IClock clock)
{
    /// <summary>定义一个计划任务。</summary>
    /// <param name="code">任务编码，必须唯一。</param>
    /// <param name="interval">执行间隔，必须为正。</param>
    /// <param name="firstRunAt">首次执行时刻；不传则以"现在"起算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时返回任务；编码已存在或间隔非法则失败。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> 为 <c>null</c>。</exception>
    public async Task<Result<ScheduledTask>> DefineAsync(
        TaskCode code,
        TimeSpan interval,
        DateTimeOffset? firstRunAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        var existing = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(task => task.Code.Equals(code)))
        {
            return Result.Failure<ScheduledTask>(new Error(
                "scheduling.task_code.taken",
                $"任务编码已存在：{code.Value}。"));
        }

        var created = ScheduledTask.Create(
            new ScheduledTaskId(ids.NextId()),
            code,
            interval,
            firstRunAt ?? clock.UtcNow);

        if (created.IsFailure)
        {
            return created;
        }

        await store.SaveAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>列出全部任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按编码排序的任务。</returns>
    public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    /// <summary>按标识找一个任务。</summary>
    /// <param name="id">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回任务，否则 <c>null</c>。</returns>
    public async Task<ScheduledTask?> FindAsync(
        ScheduledTaskId id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        var all = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return all.Count == 0 ? null : all.FirstOrDefault(task => task.Id.Equals(id));
    }

    /// <summary>停用一个任务。</summary>
    /// <param name="id">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功，或任务不存在。</returns>
    public Task<Result> PauseAsync(ScheduledTaskId id, CancellationToken cancellationToken = default) =>
        ChangeEnabledAsync(id, enable: false, cancellationToken);

    /// <summary>启用一个任务，并从"现在"重新起算下次执行。</summary>
    /// <param name="id">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功，或任务不存在。</returns>
    public Task<Result> ResumeAsync(ScheduledTaskId id, CancellationToken cancellationToken = default) =>
        ChangeEnabledAsync(id, enable: true, cancellationToken);

    private async Task<Result> ChangeEnabledAsync(
        ScheduledTaskId id,
        bool enable,
        CancellationToken cancellationToken)
    {
        var task = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return Result.Failure(new Error("scheduling.task.not_found", $"任务不存在：{id.Value}。"));
        }

        if (enable)
        {
            // 从"现在"起算，而不是补跑停用期间欠下的那些次。
            task.Enable(clock.UtcNow);
        }
        else
        {
            task.Disable();
        }

        await store.SaveAsync(task, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
