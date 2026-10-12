using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;

namespace NexusStackNext.Identity.Application;

/// <summary>按观察到的角色版本修改展示名，不改变编码或授权。</summary>
/// <param name="RoleId">角色标识。</param>
/// <param name="ExpectedVersion">条件写入版本。</param>
/// <param name="Name">新展示名。</param>
public sealed record RenameRoleCommand(long RoleId, long ExpectedVersion, string Name) : ICommand, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [RoleId];
}

/// <summary>在事务开始前拒绝无效版本与名称。</summary>
public sealed class RenameRoleValidator : IRequestValidator<RenameRoleCommand>
{
    /// <inheritdoc />
    public Result Validate(RenameRoleCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedVersion <= 0) { return Result.Failure(new("identity.role.version_invalid", "必须提供正数的角色版本。")); }
        var name = RoleName.Create(request.Name);
        return name.IsSuccess ? Result.Success() : Result.Failure(name.Error);
    }
}

/// <summary>只改变一个角色；同名为空操作，改名不影响权限缓存。</summary>
/// <param name="roles">所属角色仓储。</param>
public sealed class RenameRoleHandler(IRoleRepository roles) : ICommandHandler<RenameRoleCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(RenameRoleCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var role = (await roles.FindManyAsync([new RoleId(command.RoleId)], cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (role is null) { return Result.Failure(AuthorizationManagementErrors.RoleNotFound); }
        if (role.Version != command.ExpectedVersion) { return Result.Failure(AuthorizationManagementErrors.RoleConflict); }
        role.Rename(RoleName.Create(command.Name).Value);
        return Result.Success();
    }
}

/// <summary>按已观察版本启停一个角色。</summary>
/// <param name="RoleId">角色标识。</param>
/// <param name="ExpectedVersion">条件写入版本。</param>
/// <param name="Enabled">目标状态。</param>
public sealed record SetRoleEnabledCommand(long RoleId, long ExpectedVersion, bool Enabled) : ICommand, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [RoleId];
}

/// <summary>事务开始前拒绝无效角色版本。</summary>
public sealed class SetRoleEnabledValidator : IRequestValidator<SetRoleEnabledCommand>
{
    /// <inheritdoc />
    public Result Validate(SetRoleEnabledCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ExpectedVersion > 0 ? Result.Success()
            : Result.Failure(new("identity.role.version_invalid", "必须提供正数的角色版本。"));
    }
}

/// <summary>只修改 Role 聚合，实际变化提交后才失效权限缓存。</summary>
/// <param name="roles">所属角色仓储。</param>
/// <param name="clock">变更时刻。</param>
/// <param name="transaction">提交边界。</param>
public sealed class SetRoleEnabledHandler(IRoleRepository roles, IClock clock, IdentityCommandTransaction transaction)
    : ICommandHandler<SetRoleEnabledCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(SetRoleEnabledCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var role = (await roles.FindManyAsync([new RoleId(command.RoleId)], cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (role is null) { return Result.Failure(AuthorizationManagementErrors.RoleNotFound); }
        if (role.Version != command.ExpectedVersion) { return Result.Failure(AuthorizationManagementErrors.RoleConflict); }
        var result = role.SetEnabled(command.Enabled, clock.UtcNow);
        if (result.IsSuccess && role.Version != command.ExpectedVersion) { transaction.InvalidatePermissionsAfterCommit(); }
        return result;
    }
}
