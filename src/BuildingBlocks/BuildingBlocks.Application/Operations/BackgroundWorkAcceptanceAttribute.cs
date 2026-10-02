namespace NexusStackNext.BuildingBlocks.Application.Operations;

/// <summary>该命令成功只代表后台工作已受理；完成结果由后续任务执行报告。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BackgroundWorkAcceptanceAttribute : Attribute;
