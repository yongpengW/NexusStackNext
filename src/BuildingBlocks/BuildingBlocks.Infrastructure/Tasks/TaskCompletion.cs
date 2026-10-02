namespace NexusStackNext.BuildingBlocks.Infrastructure.Tasks;

/// <summary>业务结果应用之后的有限结论；持久化状态由执行协议统一映射。</summary>
public enum TaskCompletion
{
    /// <summary>结果已应用，或当前结果已相同。</summary>
    Succeeded,
    /// <summary>输入已过期，不再应用此结果。</summary>
    Superseded,
}
