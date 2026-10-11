using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application;

/// <summary>必须基于已观察菜单树版本执行的条件命令。</summary>
public interface IExpectedMenuTreeVersion
{
    /// <summary>调用方观察到的菜单树聚合版本。</summary>
    long ExpectedVersion { get; }
}

/// <summary>菜单树版本的稳定拒绝。</summary>
public static class MenuLifecycleErrors
{
    /// <summary>菜单树在读取后已发生变化。</summary>
    public static Error Conflict { get; } = new("identity.menu.conflict", "菜单树已变化，请重新读取后再提交。");
}

/// <summary>菜单树版本不能缺省或为负数。</summary>
public sealed class ExpectedMenuTreeVersionValidator : IRequestValidator<IExpectedMenuTreeVersion>
{
    /// <inheritdoc />
    public Result Validate(IExpectedMenuTreeVersion request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ExpectedVersion > 0 ? Result.Success()
            : Result.Failure(new("identity.menu.version_invalid", "必须提供正数的菜单树版本。"));
    }
}

/// <summary>按菜单树聚合版本删除一个叶节点。</summary>
/// <param name="MenuId">目标菜单节点。</param>
/// <param name="ExpectedVersion">调用方观察到的菜单树版本。</param>
public sealed record RemoveMenuCommand(long MenuId, long ExpectedVersion)
    : ICommand, IIdentifiedRequest, IExpectedMenuTreeVersion
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [MenuId];
}

/// <summary>删除菜单节点并在提交后失效权限投影。</summary>
/// <param name="trees">菜单树聚合。</param>
/// <param name="transaction">Identity 提交边界。</param>
public sealed class RemoveMenuHandler(IMenuTreeRepository trees, IdentityCommandTransaction transaction)
    : ICommandHandler<RemoveMenuCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(RemoveMenuCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tree = await trees.FindAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || tree.Find(new MenuId(command.MenuId)) is null)
        { return Result.Failure(IdentityErrors.MenuNotFound(command.MenuId)); }
        if (tree.Version != command.ExpectedVersion) { return Result.Failure(MenuLifecycleErrors.Conflict); }
        var result = tree.Remove(new MenuId(command.MenuId));
        if (result.IsSuccess && tree.Version != command.ExpectedVersion)
        { transaction.InvalidatePermissionsAfterCommit(); }
        return result;
    }
}
