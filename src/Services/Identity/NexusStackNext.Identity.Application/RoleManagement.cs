using NexusStackNext.BuildingBlocks.Application.Messaging;
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
