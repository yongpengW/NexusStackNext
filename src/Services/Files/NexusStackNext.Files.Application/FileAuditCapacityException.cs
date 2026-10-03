using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Files.Application;

/// <summary>事实容量不足，文件元数据与整批事实均未提交。</summary>
public sealed class FileAuditCapacityException : Exception
{
    /// <summary>可安全返回调用方的容量拒绝原因。</summary>
    public static readonly Error Error = new("files.audit_capacity.exhausted", "文件审计容量暂不可用，请稍后重试。");

    /// <summary>创建不含存储细节的容量拒绝。</summary>
    public FileAuditCapacityException() : base(Error.Message) { }
}
