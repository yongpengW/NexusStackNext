using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Infrastructure;

internal sealed class MemorySessionStateReader(IdentityMemoryState state, IdentitySessionReadOptions options) : ISessionStateReader, IAccessStateReader, IDisposable
{
    private readonly SemaphoreSlim _permits = new(options.MaxConcurrency, options.MaxConcurrency);
    public async Task<Result<SessionState?>> ReadAsync(long userId, CancellationToken cancellationToken = default)
    {
        var current = await ReadCurrentAsync(userId, null, cancellationToken).ConfigureAwait(false);
        return current.IsSuccess ? Result.Success(current.Value?.Session) : Result.Failure<SessionState?>(current.Error);
    }

    public Task<Result<AccessState?>> ReadAsync(long userId, PermissionKey required, CancellationToken cancellationToken = default)
        => ReadCurrentAsync(userId, required, cancellationToken);

    private Task<Result<AccessState?>> ReadCurrentAsync(long userId, PermissionKey? required, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_permits.Wait(0, cancellationToken)) { return Unavailable(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        try
        {
            using (state.Capacity.Enter(budget.Token))
            {
                AccessState? current = null;
                if (state.Data.Users.TryGetValue(new UserId(userId), out var user))
                {
                    var allowed = user.IsBuiltIn;
                    if (!allowed && required is { } operation)
                    {
                        var menus = user.RoleIds.Where(state.Data.Roles.ContainsKey)
                            .SelectMany(id => state.Data.Roles[id].GrantedMenuIds).ToHashSet();
                        var existingMenus = state.Data.Trees.Values.SelectMany(tree => tree.Nodes).Select(node => node.Id).ToHashSet();
                        allowed = state.Data.Resources.Values.Any(resource => resource.MenuId is { } menu
                            && menus.Contains(menu) && existingMenus.Contains(menu) && resource.PermissionKey == operation);
                    }
                    current = new(new(user.SessionVersion, user.IsEnabled, user.IsBuiltIn), allowed);
                }
                budget.Token.ThrowIfCancellationRequested();
                return Task.FromResult(Result.Success(current));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (CommittedFactCapacityBusyException) { return Unavailable(); }
        finally { _permits.Release(); }
    }

    private static Task<Result<AccessState?>> Unavailable() => Task.FromResult(Result.Failure<AccessState?>(SessionValidationErrors.Unavailable));
    public void Dispose() => _permits.Dispose();
}
