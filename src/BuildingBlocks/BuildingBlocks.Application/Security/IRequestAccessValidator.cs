using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;

namespace NexusStackNext.BuildingBlocks.Application.Security;

/// <summary>依据同一次权威读取验证会话及端点许可；最终允许结论不得跨请求缓存。</summary>
public interface IRequestAccessValidator
{
    /// <summary>判定代码声明的操作，根旁路也须通过当前会话验证。</summary>
    /// <param name="userId">已认证主体。</param>
    /// <param name="sessionVersion">已验签会话版本。</param>
    /// <param name="required">端点元数据声明的权限键，不取自客户端正文或 URL。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>允许或无权；无效会话与权威故障是不同失败。</returns>
    Task<Result<bool>> ValidateAsync(string userId, long? sessionVersion, PermissionKey required, CancellationToken cancellationToken = default);
}
