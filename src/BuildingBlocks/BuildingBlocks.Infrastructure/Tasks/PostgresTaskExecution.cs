using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Tasks;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tasks;

/// <summary>Pricing 与 Costing 共用的短事务执行协议；业务输入和结果仍由各上下文拥有。</summary>
/// <typeparam name="TTask">包含本上下文输入快照的执行记录。</typeparam>
public sealed class PostgresTaskExecution<TTask> where TTask : DurableTaskRecord
{
    private readonly DbContext _database;
    private readonly DurableTaskOptions _options;
    private readonly string _schema;

    /// <summary>在所属上下文的连接上组装执行协议。</summary>
    /// <param name="database">所属上下文。</param>
    /// <param name="schema">代码中固定的 schema，也是操作错误码前缀。</param>
    /// <param name="options">有界执行策略。</param>
    public PostgresTaskExecution(DbContext database, string schema, DurableTaskOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (schema.Any(c => c is not (>= 'a' and <= 'z') && c != '_')) { throw new ArgumentException("schema 必须为小写标识符。", nameof(schema)); }
        options.Validate();
        _database = database;
        _schema = schema;
        _options = options;
    }

    /// <summary>领取一项到期任务；过期执行在新代次出现前结束，预算不自动重置。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已提交租约的工作，或没有工作。</returns>
    public async Task<TTask?> ClaimAsync(CancellationToken cancellationToken = default)
    {
        _database.ChangeTracker.Clear();
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"""
            SELECT * FROM "{_schema}".tasks
            WHERE ("State" IN ('Pending', 'Retry') AND "AvailableAt" <= clock_timestamp())
               OR ("State" = 'Running' AND "LeaseUntil" <= clock_timestamp())
            ORDER BY "AvailableAt", "TaskId" LIMIT 1 FOR UPDATE SKIP LOCKED
            """;
        var task = (await _database.Set<TTask>().FromSqlRaw(sql).ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (task is null) { return null; }
        var now = await NowAsync(cancellationToken).ConfigureAwait(false);
        if (task.State == "Running") { await FinishAttemptAsync(task, "Expired", now, _schema + ".lease_expired", cancellationToken).ConfigureAwait(false); }
        if (task.Attempts >= _options.MaxAttempts)
        {
            task.State = "Failed";
            task.ErrorCode = _schema + ".attempts_exhausted";
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        task.State = "Running";
        task.Epoch++;
        task.Attempts++;
        task.ErrorCode = null;
        task.LeaseUntil = now + _options.LeaseDuration;
        task.MaxLeaseUntil = now + _options.MaxLeaseDuration;
        _database.Set<DurableTaskAttempt>().Add(new DurableTaskAttempt { TaskId = task.TaskId, Epoch = task.Epoch, StartedAt = now });
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    /// <summary>在租约保护下提交业务结果、可选 Outbox 及执行结论。耗时计算必须在调用前完成。</summary>
    /// <param name="taskId">工作标识。</param>
    /// <param name="epoch">执行代次。</param>
    /// <param name="applyResult">只做本地数据库修改，返回 Succeeded 或 Superseded；不得调用外部服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>租约仍有效并已提交。</returns>
    public async Task<bool> CompleteAsync(Guid taskId, long epoch, Func<TTask, CancellationToken, Task<TaskCompletion>> applyResult, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applyResult);
        _database.ChangeTracker.Clear();
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await LockAsync(taskId, cancellationToken).ConfigureAwait(false);
        var now = await NowAsync(cancellationToken).ConfigureAwait(false);
        if (!Owns(task, epoch, now)) { return false; }
        task!.State = await applyResult(task, cancellationToken).ConfigureAwait(false) switch
        {
            TaskCompletion.Succeeded => "Succeeded",
            TaskCompletion.Superseded => "Superseded",
            _ => throw new InvalidOperationException("结果应用必须给出明确的终态。"),
        };
        await FinishAttemptAsync(task, task.State, now, null, cancellationToken).ConfigureAwait(false);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (task.LeaseUntil <= await NowAsync(cancellationToken).ConfigureAwait(false)) { return false; }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>记录失败并安排有限重试；旧代次没有写入权。</summary>
    /// <param name="taskId">工作标识。</param>
    /// <param name="epoch">执行代次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>失败已记录。</returns>
    public async Task<bool> FailAsync(Guid taskId, long epoch, CancellationToken cancellationToken = default)
    {
        _database.ChangeTracker.Clear();
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await LockAsync(taskId, cancellationToken).ConfigureAwait(false);
        var now = await NowAsync(cancellationToken).ConfigureAwait(false);
        if (!Owns(task, epoch, now)) { return false; }
        task!.State = task.Attempts >= _options.MaxAttempts ? "Failed" : "Retry";
        task.ErrorCode = _schema + ".calculation_failed";
        task.AvailableAt = now + _options.RetryDelay * task.Attempts;
        await FinishAttemptAsync(task, "Failed", now, task.ErrorCode, cancellationToken).ConfigureAwait(false);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (task.LeaseUntil <= await NowAsync(cancellationToken).ConfigureAwait(false)) { return false; }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>人工重新开放失败任务的预算，保留代次与历史。</summary>
    /// <param name="taskId">工作标识。</param>
    /// <param name="expectedEpoch">操作者看到的执行代次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重新开放的任务或冲突。</returns>
    public async Task<Result<TTask>> RetryAsync(Guid taskId, long expectedEpoch, CancellationToken cancellationToken = default)
    {
        _database.ChangeTracker.Clear();
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await LockAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null) { return Result.Failure<TTask>(new Error(_schema + ".not_found", "任务不存在。")); }
        if (task.State != "Failed" || task.Epoch != expectedEpoch) { return Result.Failure<TTask>(new Error(_schema + ".retry_conflict", "任务状态或执行代次已经改变。")); }
        task.State = "Retry";
        task.Attempts = 0;
        task.ErrorCode = null;
        task.LeaseUntil = null;
        task.MaxLeaseUntil = null;
        task.AvailableAt = await NowAsync(cancellationToken).ConfigureAwait(false);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _database.Entry(task).Collection(x => x.History).LoadAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(task);
    }

    /// <summary>延长仍有效的执行权；本次领取的总期限保持不变。</summary>
    /// <param name="taskId">工作标识。</param>
    /// <param name="epoch">当前领取代次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功持久化的新租约，或执行权冲突；不增加代次及尝试次数。</returns>
    public async Task<Result<TTask>> RenewAsync(Guid taskId, long epoch, CancellationToken cancellationToken = default)
    {
        _database.ChangeTracker.Clear();
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await LockAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null) { return Result.Failure<TTask>(new Error(_schema + ".not_found", "任务不存在。")); }
        var now = await NowAsync(cancellationToken).ConfigureAwait(false);
        if (!Owns(task, epoch, now) || task.MaxLeaseUntil is null)
        {
            return Result.Failure<TTask>(new Error(_schema + ".renew_conflict", "原租约已经失效，或没有可续租的执行预算。"));
        }
        var originalDeadline = task.LeaseUntil!.Value;
        var proposed = now + _options.LeaseDuration;
        var deadline = proposed < task.MaxLeaseUntil.Value ? proposed : task.MaxLeaseUntil.Value;
        if (deadline <= originalDeadline)
        {
            return Result.Failure<TTask>(new Error(_schema + ".renew_conflict", "本次领取的租约不能再延长。"));
        }
        task.LeaseUntil = deadline;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // 保存时可能等待锁或触发器。必须复检原期限，不能以刚写的新期限证明原执行权。
        if (originalDeadline <= await NowAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<TTask>(new Error(_schema + ".renew_conflict", "原租约在续租提交前已失效。"));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(task);
    }

    /// <summary>条件取消尚未终结的工作；已接受输入、既有结果与历史均保留。</summary>
    /// <param name="taskId">工作标识。</param>
    /// <param name="expectedEpoch">操作者观察到的执行代次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>同代次的取消结论、未找到或状态冲突。</returns>
    public async Task<Result<TTask>> CancelAsync(Guid taskId, long expectedEpoch, CancellationToken cancellationToken = default)
    {
        _database.ChangeTracker.Clear();
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await LockAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null) { return Result.Failure<TTask>(new Error(_schema + ".not_found", "任务不存在。")); }
        if (task.Epoch != expectedEpoch || task.State is not ("Pending" or "Retry" or "Running" or "Cancelled"))
        {
            return Result.Failure<TTask>(new Error(_schema + ".cancel_conflict", "任务状态或执行代次已经改变。"));
        }
        if (task.State != "Cancelled")
        {
            if (task.State == "Running")
            {
                await FinishAttemptAsync(task, "Cancelled", await NowAsync(cancellationToken).ConfigureAwait(false), null, cancellationToken).ConfigureAwait(false);
            }
            task.State = "Cancelled";
            task.LeaseUntil = null;
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await _database.Entry(task).Collection(x => x.History).LoadAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(task);
    }

    private Task<DateTimeOffset> NowAsync(CancellationToken token) =>
        _database.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
    private async Task<TTask?> LockAsync(Guid taskId, CancellationToken token)
    {
        var sql = $"SELECT * FROM \"{_schema}\".tasks WHERE \"TaskId\" = {{0}} FOR UPDATE";
        return (await _database.Set<TTask>().FromSqlRaw(sql, taskId).ToListAsync(token).ConfigureAwait(false)).SingleOrDefault();
    }
    private static bool Owns(TTask? task, long epoch, DateTimeOffset now) =>
        task is { State: "Running" } && task.Epoch == epoch && task.LeaseUntil > now;
    private async Task FinishAttemptAsync(TTask task, string outcome, DateTimeOffset now, string? errorCode, CancellationToken token)
    {
        var attempt = await _database.Set<DurableTaskAttempt>().SingleAsync(x => x.TaskId == task.TaskId && x.Epoch == task.Epoch, token).ConfigureAwait(false);
        attempt.Outcome = outcome;
        attempt.FinishedAt = now;
        attempt.ErrorCode = errorCode;
    }
}
