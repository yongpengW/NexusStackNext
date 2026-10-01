using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>登出：撤销这个用户已发出的全部访问令牌与刷新令牌。</summary>
/// <param name="UserId">用户标识——由已认证的身份给出，不由请求体。</param>
public sealed record LogoutCommand(long UserId) : ICommand, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>只修改用户聚合的会话版本，同时使旧访问令牌与刷新令牌失效。</summary>
/// <param name="users">持有会话版本的用户仓储。</param>
public sealed class LogoutHandler(IUserRepository users) : ICommandHandler<LogoutCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        LogoutCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var userId = new Domain.Ids.UserId(command.UserId);
        var user = await users.FindAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(Domain.IdentityErrors.UserNotFound());
        }

        user.RevokeSessions();
        return Result.Success();
    }
}
