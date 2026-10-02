using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application;

/// <summary>读取用户当前可接受的会话版本；不存在或禁用时拒绝。</summary>
/// <param name="UserId">用户标识。</param>
public sealed record GetSessionVersionQuery(long UserId) : IQuery<long>, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>会话版本从用户的权威状态读取，不使用进程缓存。</summary>
/// <param name="users">用户仓储。</param>
public sealed class GetSessionVersionHandler(IUserRepository users) : IQueryHandler<GetSessionVersionQuery, long>, ISessionValidator
{
    /// <inheritdoc />
    public async Task<bool> IsCurrentAsync(string userId, long? sessionVersion, CancellationToken cancellationToken = default)
    {
        if (sessionVersion is null || !long.TryParse(userId, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return false;
        }

        var current = await HandleAsync(new GetSessionVersionQuery(id), cancellationToken).ConfigureAwait(false);
        return current.IsSuccess && current.Value == sessionVersion;
    }

    /// <inheritdoc />
    public async Task<Result<long>> HandleAsync(GetSessionVersionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var user = await users.FindAsync(new UserId(query.UserId), cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<long>(IdentityErrors.UserNotFound());
        }

        return user.IsEnabled ? Result.Success(user.SessionVersion) : Result.Failure<long>(IdentityErrors.UserDisabled());
    }
}
