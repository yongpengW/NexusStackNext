using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

// 查询不进入命令事务；只将已知的存储忙拒绝适配为查询本来的 Result 契约。
internal sealed class IdentityQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
    : IQueryHandler<TQuery, TResult> where TQuery : IQuery<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        try { return await handler.HandleAsync(query, cancellationToken).ConfigureAwait(false); }
        catch (CommittedFactCapacityBusyException) { return Result.Failure<TResult>(CommittedFactCapacityBusyException.Reason); }
    }
}
