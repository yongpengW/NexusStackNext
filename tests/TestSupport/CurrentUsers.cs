using NexusStackNext.BuildingBlocks.Application.Security;

namespace NexusStackNext.TestSupport;

/// <summary>固定的测试操作者。</summary>
/// <param name="userId">用户标识；系统为空。</param>
/// <param name="isRoot">是否为根操作者。</param>
/// <param name="sessionVersion">会话版本。</param>
public sealed class FixedCurrentUser(string? userId, bool isRoot = false, long? sessionVersion = null) : ICurrentUser
{
    /// <inheritdoc />
    public string? UserId => userId;
    /// <inheritdoc />
    public bool IsRoot => isRoot;
    /// <inheritdoc />
    public long? SessionVersion => sessionVersion;
}

/// <summary>用于同一应用先后收到不同操作者请求的测试。</summary>
/// <param name="userId">初始操作者。</param>
public sealed class MutableCurrentUser(string? userId) : ICurrentUser
{
    /// <inheritdoc />
    public string? UserId { get; set; } = userId;
    /// <inheritdoc />
    public bool IsRoot { get; set; }
    /// <inheritdoc />
    public long? SessionVersion { get; set; }
}
