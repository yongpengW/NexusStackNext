namespace NexusStackNext.BuildingBlocks.Application.Operations;

/// <summary>命令显式声明它操作的任务；仅供日志关联，不证明任务存在、命令获准或任务完成。</summary>
public interface ITaskOperationCommand
{
    /// <summary>所属上下文的任务标识，不读取或序列化其它命令内容。</summary>
    Guid TaskId { get; }
}
