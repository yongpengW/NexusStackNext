using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application;

/// <summary>必须基于已经观察的用户版本进行的条件命令。</summary>
public interface IExpectedUserVersion
{
    /// <summary>调用方观察的用户聚合版本。</summary>
    long ExpectedVersion { get; }
}

/// <summary>版本不能缺省或为负数。</summary>
public sealed class ExpectedUserVersionValidator : IRequestValidator<IExpectedUserVersion>
{
    /// <inheritdoc />
    public Result Validate(IExpectedUserVersion request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ExpectedVersion > 0 ? Result.Success()
            : Result.Failure(new("identity.user.version_invalid", "必须提供正数的用户版本。"));
    }
}

/// <summary>用户生命周期的稳定错误。</summary>
public static class UserLifecycleErrors
{
    /// <summary>已观察的用户状态已经改变，调用方应重新读取。</summary>
    public static Error Conflict { get; } = new("identity.user.conflict", "用户状态已变化，请重新读取后再提交。");
}

/// <summary>按已观察版本启停用户，不修改根身份。</summary>
/// <param name="UserId">目标用户。</param>
/// <param name="ExpectedVersion">已经观察的聚合版本。</param>
/// <param name="Enabled">是否启用。</param>
public sealed record SetUserEnabledCommand(long UserId, long ExpectedVersion, bool Enabled)
    : ICommand, IIdentifiedRequest, IExpectedUserVersion
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId];
}

/// <summary>只改变一个 User 聚合；提交后才失效权限缓存。</summary>
/// <param name="users">用户仓储。</param>
/// <param name="clock">变更时刻。</param>
/// <param name="transaction">提交边界。</param>
public sealed class SetUserEnabledHandler(IUserRepository users, IClock clock, IdentityCommandTransaction transaction)
    : ICommandHandler<SetUserEnabledCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(SetUserEnabledCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindAsync(new UserId(command.UserId), cancellationToken).ConfigureAwait(false);
        if (user is null) { return Result.Failure(IdentityErrors.UserNotFound()); }
        if (user.Version != command.ExpectedVersion) { return Result.Failure(UserLifecycleErrors.Conflict); }
        var result = command.Enabled ? user.Enable(clock.UtcNow) : user.Disable(clock.UtcNow);
        if (result.IsSuccess && user.Version != command.ExpectedVersion) { transaction.InvalidatePermissionsAfterCommit(); }
        return result;
    }
}
