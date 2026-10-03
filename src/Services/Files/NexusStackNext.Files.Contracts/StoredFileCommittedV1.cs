using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Files.Contracts;

/// <summary>Files 已提交的文件生命周期事实；不含文件名、内容、存储句柄或访问地址。</summary>
public sealed record StoredFileCommittedV1 : IntegrationEvent
{
    /// <summary>稳定事件名。</summary>
    public const string Name = "files.stored-file-committed.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>Files 拥有的文件标识。</summary>
    public required long FileId { get; init; }
    /// <summary>明确的生命周期变化。</summary>
    public required string Operation { get; init; }
    /// <summary>文件聚合的提交版本。</summary>
    public required long Version { get; init; }
    /// <summary>当前认证的执行者；系统执行为空。</summary>
    public string? ActorId { get; init; }
    /// <summary>来源追踪标识。</summary>
    public required string TraceId { get; init; }
    /// <summary>调查关联标识。</summary>
    public required string CorrelationId { get; init; }
    /// <summary>当前操作与原发起关系，不能作为授权凭据。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionOrigin? Execution { get; init; }
}
