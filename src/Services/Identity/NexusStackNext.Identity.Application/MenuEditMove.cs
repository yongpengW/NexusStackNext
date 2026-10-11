using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;

namespace NexusStackNext.Identity.Application;

/// <summary>按菜单树版本修改节点内容。</summary>
/// <param name="MenuId">目标节点。</param>
/// <param name="ExpectedVersion">调用方观察到的树版本。</param>
/// <param name="Title">新标题。</param>
/// <param name="SortOrder">新排序。</param>
public sealed record UpdateMenuCommand(long MenuId, long ExpectedVersion, string Title, int SortOrder)
    : ICommand, IIdentifiedRequest, IExpectedMenuTreeVersion
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [MenuId];
}

/// <summary>按菜单树版本移动节点及其整棵子树。</summary>
/// <param name="MenuId">目标节点。</param>
/// <param name="ExpectedVersion">调用方观察到的树版本。</param>
/// <param name="NewParentMenuId">新父节点；null 表示移动到根。</param>
public sealed record MoveMenuCommand(long MenuId, long ExpectedVersion, long? NewParentMenuId)
    : ICommand, IIdentifiedRequest, IExpectedMenuTreeVersion
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => NewParentMenuId is { } parent ? [MenuId, parent] : [MenuId];
}

/// <summary>修改菜单标题与排序，不改变授权挂载点。</summary>
public sealed class UpdateMenuHandler(IMenuTreeRepository trees) : ICommandHandler<UpdateMenuCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(UpdateMenuCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var title = MenuTitle.Create(command.Title);
        if (title.IsFailure) { return Result.Failure(title.Error); }
        var tree = await trees.FindAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || tree.Find(new MenuId(command.MenuId)) is null)
        { return Result.Failure(IdentityErrors.MenuNotFound(command.MenuId)); }
        if (tree.Version != command.ExpectedVersion) { return Result.Failure(MenuLifecycleErrors.Conflict); }
        return tree.Update(new MenuId(command.MenuId), title.Value, command.SortOrder);
    }
}

/// <summary>移动菜单节点及后代，不改变角色授予的菜单标识。</summary>
public sealed class MoveMenuHandler(IMenuTreeRepository trees) : ICommandHandler<MoveMenuCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(MoveMenuCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tree = await trees.FindAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || tree.Find(new MenuId(command.MenuId)) is null)
        { return Result.Failure(IdentityErrors.MenuNotFound(command.MenuId)); }
        if (command.NewParentMenuId is { } parent && tree.Find(new MenuId(parent)) is null)
        { return Result.Failure(IdentityErrors.MenuNotFound(parent)); }
        if (tree.Version != command.ExpectedVersion) { return Result.Failure(MenuLifecycleErrors.Conflict); }
        return tree.Move(new MenuId(command.MenuId), command.NewParentMenuId is { } id ? new MenuId(id) : null);
    }
}
