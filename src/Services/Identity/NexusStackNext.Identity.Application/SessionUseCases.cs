using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application;

/// <summary>判定当前已验签主体的会话，不接受请求体选定主体。</summary>
/// <param name="UserId">已验签主体。</param>
/// <param name="SessionVersion">已验签会话版本。</param>
public sealed record GetCurrentSessionQuery(long UserId, long? SessionVersion) : IQuery<CurrentSessionV1>;

/// <summary>以当前用户状态判定会话与根身份，允许结果不缓存。</summary>
/// <param name="states">Identity 自己的有界权威读取。</param>
public sealed class GetCurrentSessionHandler(ISessionStateReader states) : IQueryHandler<GetCurrentSessionQuery, CurrentSessionV1>, ISessionValidator
{
    /// <inheritdoc />
    public async Task<Result<CurrentSessionV1>> HandleAsync(GetCurrentSessionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.UserId <= 0 || query.SessionVersion is null or < 0) { return Invalid(); }
        var read = await states.ReadAsync(query.UserId, cancellationToken).ConfigureAwait(false);
        if (read.IsFailure) { return Result.Failure<CurrentSessionV1>(SessionValidationErrors.Unavailable); }
        return read.Value is { IsEnabled: true } user && user.SessionVersion == query.SessionVersion
            ? Result.Success(new CurrentSessionV1(1, query.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), user.SessionVersion, user.IsRoot))
            : Invalid();
    }

    /// <inheritdoc />
    public async Task<Result<ValidatedSession>> ValidateAsync(string userId, long? sessionVersion, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(userId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        { return Result.Failure<ValidatedSession>(SessionValidationErrors.Invalid); }
        var current = await HandleAsync(new(id, sessionVersion), cancellationToken).ConfigureAwait(false);
        return current.IsSuccess ? Result.Success(new ValidatedSession(current.Value.IsRoot)) : Result.Failure<ValidatedSession>(current.Error);
    }

    private static Result<CurrentSessionV1> Invalid() => Result.Failure<CurrentSessionV1>(SessionValidationErrors.Invalid);
}

/// <summary>读取用户当前可接受的会话版本；不存在或禁用时拒绝。</summary>
/// <param name="UserId">用户标识。</param>
public sealed record GetSessionVersionQuery(long UserId) : IQuery<long>, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>会话版本从用户的权威状态读取，不使用进程缓存。</summary>
/// <param name="users">用户仓储。</param>
public sealed class GetSessionVersionHandler(IUserRepository users) : IQueryHandler<GetSessionVersionQuery, long>
{
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
