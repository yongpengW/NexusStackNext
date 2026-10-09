using System.Globalization;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Identity.Contracts;

namespace NexusStackNext.Identity.Application;

/// <summary>同一已提交视图中的会话与操作许可。</summary>
/// <param name="Session">最小用户事实。</param>
/// <param name="IsAllowed">当前根身份或有效的角色、菜单及资源链授予该操作。</param>
public sealed record AccessState(SessionState Session, bool IsAllowed);

/// <summary>Identity 所属数据的一次有界权威读取；不同存储在这个缝上变化。</summary>
public interface IAccessStateReader
{
    /// <summary>一次读取当前已提交状态，不能拼接跟踪实体或缓存的权限。</summary>
    /// <param name="userId">主体。</param>
    /// <param name="required">代码声明的操作。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>当前事实；不存在返回空，读取故障返回失败。</returns>
    Task<Result<AccessState?>> ReadAsync(long userId, PermissionKey required, CancellationToken cancellationToken = default);
}

/// <summary>当前已验签主体的单操作判定。</summary>
/// <param name="UserId">认证主体。</param>
/// <param name="SessionVersion">认证会话。</param>
/// <param name="Required">规范操作键。</param>
public sealed record GetCurrentAccessQuery(long UserId, long? SessionVersion, PermissionKey Required) : IQuery<CurrentAccessV1>;

/// <summary>允许结果只属于本次调用；根旁路与普通许可共用当前会话事实。</summary>
/// <param name="states">Identity 自己的权威读取。</param>
public sealed class GetCurrentAccessHandler(IAccessStateReader states) : IQueryHandler<GetCurrentAccessQuery, CurrentAccessV1>, IRequestAccessValidator
{
    /// <inheritdoc />
    public async Task<Result<CurrentAccessV1>> HandleAsync(GetCurrentAccessQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.UserId <= 0 || query.SessionVersion is null or < 0) { return Invalid(); }
        if (string.IsNullOrEmpty(query.Required.Value)) { return Result.Failure<CurrentAccessV1>(new("identity.access.key_invalid", "必须声明操作权限键。")); }
        var current = await states.ReadAsync(query.UserId, query.Required, cancellationToken).ConfigureAwait(false);
        if (current.IsFailure) { return Result.Failure<CurrentAccessV1>(SessionValidationErrors.Unavailable); }
        return current.Value is { Session.IsEnabled: true } state && state.Session.SessionVersion == query.SessionVersion
            ? Result.Success(new CurrentAccessV1(1, query.UserId.ToString(CultureInfo.InvariantCulture), state.Session.SessionVersion, query.Required.Value, state.IsAllowed))
            : Invalid();
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ValidateAsync(string userId, long? sessionVersion, PermissionKey required, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) { return Result.Failure<bool>(SessionValidationErrors.Invalid); }
        var decision = await HandleAsync(new(id, sessionVersion, required), cancellationToken).ConfigureAwait(false);
        return decision.IsSuccess ? Result.Success(decision.Value.IsAllowed) : Result.Failure<bool>(decision.Error);
    }

    private static Result<CurrentAccessV1> Invalid() => Result.Failure<CurrentAccessV1>(SessionValidationErrors.Invalid);
}
