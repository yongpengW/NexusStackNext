namespace NexusStackNext.BuildingBlocks.Application.Operations;

/// <summary>明确跳过一个内部协调命令的通用观察；不抑制已有 HTTP 或显式任务观察。</summary>
/// <param name="reason">排除原因；留空时不会排除。</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandObservationSuppressionAttribute(string reason) : Attribute
{
    /// <summary>供代码评审检查的排除原因，不采集为日志内容。</summary>
    public string Reason { get; } = reason;
}
