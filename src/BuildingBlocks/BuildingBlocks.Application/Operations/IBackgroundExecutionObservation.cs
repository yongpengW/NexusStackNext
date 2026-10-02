namespace NexusStackNext.BuildingBlocks.Application.Operations;

/// <summary>后台执行的安全描述；不携带业务输入。</summary>
/// <param name="Action">代码声明的动作名称。</param>
public abstract record BackgroundExecutionDescriptor(string Action);

/// <summary>实际计算尝试的描述；任务和租约仍由业务上下文拥有。</summary>
/// <param name="Action">代码声明的动作名称。</param>
/// <param name="TaskId">任务标识。</param>
/// <param name="Epoch">本次持有的执行代次。</param>
public sealed record TaskExecutionDescriptor(string Action, Guid TaskId, long Epoch) : BackgroundExecutionDescriptor(Action);

/// <summary>一次计划裁决的描述；计划版本与业务任务租约含义不同。</summary>
/// <param name="Action">代码声明的动作名称。</param>
/// <param name="PlanId">计划标识。</param>
/// <param name="ExpectedVersion">裁决前读取的计划版本。</param>
/// <param name="DecisionId">本次拟登记的决定标识；拒绝或失败不代表已登记。</param>
/// <param name="InitiatorId">计划持久保存的委托人，不是当前后台执行者。</param>
public sealed record ScheduleExecutionDescriptor(string Action, long PlanId, long ExpectedVersion, Guid DecisionId, string InitiatorId)
    : BackgroundExecutionDescriptor(Action);

/// <summary>所属上下文读取的执行输入与持久化来源；输入不进入日志。</summary>
/// <typeparam name="TInput">上下文私有的输入类型。</typeparam>
/// <param name="Value">后续执行使用的输入。</param>
/// <param name="Origin">业务任务受理或计划定义时保留的来源。</param>
public sealed record BackgroundExecutionInput<TInput>(TInput Value, ExecutionOrigin? Origin);

/// <summary>执行入口实际返回的结论；异常与取消由观察适配器另外识别。</summary>
public enum BackgroundExecutionOutcome
{
    /// <summary>当前执行已完成，不代替业务提交事实。</summary>
    Completed,
    /// <summary>输入已被更新的输入取代。</summary>
    Superseded,
    /// <summary>执行权已丢失，当前尝试没有提交权。</summary>
    LeaseLost,
    /// <summary>已登记下游工作意图，不代表下游完成。</summary>
    Accepted,
    /// <summary>已按计划策略登记跳过决定。</summary>
    Skipped,
    /// <summary>执行条件不满足，本次未提交。</summary>
    Rejected,
    /// <summary>执行失败，未得到预期结果。</summary>
    Failed,
}

/// <summary>围绕真实后台执行采集观察；不改变执行返回值、异常或重试政策。</summary>
public interface IBackgroundExecutionObservation
{
    /// <summary>观察一次执行；分类只用于日志，不能决定业务重试或覆盖结果。</summary>
    /// <typeparam name="TInput">执行输入，不采集其内容。</typeparam>
    /// <typeparam name="TResult">执行返回值。</typeparam>
    /// <param name="descriptor">安全执行描述。</param>
    /// <param name="prepare">读取所属上下文的输入及来源；失败和取消也需要观察。</param>
    /// <param name="execute">准备成功后的一次实际执行。</param>
    /// <param name="classify">所属上下文对已返回结果的解释。</param>
    /// <param name="cancellationToken">执行取消令牌。</param>
    /// <returns>原执行结果。</returns>
    Task<TResult> ObserveAsync<TInput, TResult>(BackgroundExecutionDescriptor descriptor, Func<Task<BackgroundExecutionInput<TInput>>> prepare, Func<TInput, Task<TResult>> execute,
        Func<TResult, BackgroundExecutionOutcome> classify, CancellationToken cancellationToken = default);
}

internal sealed class UnobservedBackgroundExecution : IBackgroundExecutionObservation
{
    public async Task<TResult> ObserveAsync<TInput, TResult>(BackgroundExecutionDescriptor descriptor, Func<Task<BackgroundExecutionInput<TInput>>> prepare,
        Func<TInput, Task<TResult>> execute, Func<TResult, BackgroundExecutionOutcome> classify, CancellationToken cancellationToken = default)
    {
        var input = await prepare().ConfigureAwait(false);
        return await execute(input.Value).ConfigureAwait(false);
    }
}
