using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Infrastructure;

internal sealed class MemorySessionStateReader(IdentityMemoryState state, IdentitySessionReadOptions options) : ISessionStateReader, IDisposable
{
    private readonly SemaphoreSlim _permits = new(options.MaxConcurrency, options.MaxConcurrency);
    public Task<Result<SessionState?>> ReadAsync(long userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_permits.Wait(0, cancellationToken)) { return Unavailable(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        try
        {
            using (state.Capacity.Enter(budget.Token))
            {
                var current = state.Data.Users.TryGetValue(new UserId(userId), out var user)
                    ? new SessionState(user.SessionVersion, user.IsEnabled, user.IsBuiltIn) : null;
                budget.Token.ThrowIfCancellationRequested();
                return Task.FromResult(Result.Success(current));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (CommittedFactCapacityBusyException) { return Unavailable(); }
        finally { _permits.Release(); }
    }

    private static Task<Result<SessionState?>> Unavailable() => Task.FromResult(Result.Failure<SessionState?>(SessionValidationErrors.Unavailable));
    public void Dispose() => _permits.Dispose();
}
