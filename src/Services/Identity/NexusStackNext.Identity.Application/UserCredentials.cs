using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Application;

/// <summary>在进入领域前检查的新口令。</summary>
public interface INewUserPassword
{
    /// <summary>只用于本次哈希或校验的明文。</summary>
    string NewPassword { get; }
}

/// <summary>新口令与注册口令使用同一强度规则。</summary>
public sealed class NewUserPasswordValidator : IRequestValidator<INewUserPassword>
{
    /// <inheritdoc />
    public Result Validate(INewUserPassword request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UserPasswordPolicy.Validate(request.NewPassword);
    }
}

/// <summary>本人轮换口令；主体与会话版本来自已认证身份。</summary>
/// <param name="UserId">当前用户。</param>
/// <param name="SessionVersion">当前已验签会话版本。</param>
/// <param name="ExpectedVersion">已经观察的聚合版本。</param>
/// <param name="OldPassword">旧明文口令。</param>
/// <param name="NewPassword">新明文口令。</param>
public sealed record ChangeOwnPasswordCommand(long UserId, long SessionVersion, long ExpectedVersion, string OldPassword, string NewPassword)
    : ICommand, IIdentifiedRequest, IExpectedUserVersion, INewUserPassword
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>管理者条件重置目标口令，HTTP 必须显式授权。</summary>
/// <param name="UserId">目标用户。</param>
/// <param name="ExpectedVersion">已经观察的聚合版本。</param>
/// <param name="NewPassword">新明文口令，无默认值。</param>
public sealed record ResetUserPasswordCommand(long UserId, long ExpectedVersion, string NewPassword)
    : ICommand, IIdentifiedRequest, IExpectedUserVersion, INewUserPassword
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>本人轮换只修改一个用户，旧口令失败不改变安全状态。</summary>
/// <param name="users">用户仓储。</param>
/// <param name="hasher">固定时间口令校验与带盐哈希。</param>
/// <param name="clock">变更时刻。</param>
/// <param name="transaction">提交边界。</param>
public sealed class ChangeOwnPasswordHandler(IUserRepository users, IPasswordHasher hasher, IClock clock, IdentityCommandTransaction transaction)
    : ICommandHandler<ChangeOwnPasswordCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(ChangeOwnPasswordCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindAsync(new UserId(command.UserId), cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsEnabled || command.SessionVersion < 0 || user.SessionVersion != command.SessionVersion)
        { return Result.Failure(SessionValidationErrors.Invalid); }
        if (user.Version != command.ExpectedVersion) { return Result.Failure(UserLifecycleErrors.Conflict); }
        if (string.IsNullOrEmpty(command.OldPassword) || !hasher.Verify(command.OldPassword, user.PasswordHash.Encoded))
        { return Result.Failure(new("identity.password.old_invalid", "旧口令不正确。")); }
        return UserCredentialRotation.Replace(user, command.NewPassword, hasher, clock.UtcNow, transaction);
    }
}

/// <summary>管理者重置只改变目标用户；相同明文不产生新的带盐哈希或会话撤销。</summary>
/// <param name="users">用户仓储。</param>
/// <param name="hasher">口令校验与哈希。</param>
/// <param name="clock">变更时刻。</param>
/// <param name="transaction">提交边界。</param>
public sealed class ResetUserPasswordHandler(IUserRepository users, IPasswordHasher hasher, IClock clock, IdentityCommandTransaction transaction)
    : ICommandHandler<ResetUserPasswordCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(ResetUserPasswordCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindAsync(new UserId(command.UserId), cancellationToken).ConfigureAwait(false);
        if (user is null) { return Result.Failure(IdentityErrors.UserNotFound()); }
        if (user.Version != command.ExpectedVersion) { return Result.Failure(UserLifecycleErrors.Conflict); }
        return UserCredentialRotation.Replace(user, command.NewPassword, hasher, clock.UtcNow, transaction);
    }
}

internal static class UserCredentialRotation
{
    internal static Result Replace(User user, string plainText, IPasswordHasher hasher, DateTimeOffset at, IdentityCommandTransaction transaction)
    {
        if (hasher.Verify(plainText, user.PasswordHash.Encoded)) { return Result.Success(); }
        var hash = PasswordHash.Create(hasher.Hash(plainText));
        if (hash.IsFailure) { return Result.Failure(hash.Error); }
        var result = user.ChangePassword(hash.Value, at);
        if (result.IsSuccess) { transaction.InvalidatePermissionsAfterCommit(); }
        return result;
    }
}
