namespace NexusStackNext.Files.Application;

/// <summary>文件元数据已由另一个请求更新；调用方应重新读取持久状态。</summary>
public sealed class FileMetadataConflictException : Exception
{
    /// <summary>创建文件版本冲突。</summary>
    public FileMetadataConflictException() : base("文件元数据版本冲突。") { }
}
