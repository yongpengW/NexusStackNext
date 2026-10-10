using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using Npgsql;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class PostgresAuditCapacity(AuditingDbContext context, AuditStorageCapacityOptions capacity) : IAuditStorageCapacityReader
{
    public Task<AuditStorageCapacitySnapshot> ReadAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        try
        {
            var pools = await context.Database.SqlQueryRaw<PoolRow>("SELECT \"Pool\", \"Records\" FROM auditing.central_storage_capacity")
                .ToArrayAsync(token).ConfigureAwait(false);
            if (pools.Length != 2 || pools.Any(pool => pool.Records < 0)) { throw new AuditStorageUnavailableException(false); }
            var facts = pools.SingleOrDefault(pool => pool.Pool == "facts");
            var observations = pools.SingleOrDefault(pool => pool.Pool == "observations");
            if (facts is null || observations is null) { throw new AuditStorageUnavailableException(false); }
            return new AuditStorageCapacitySnapshot(new(facts.Records, capacity.MaxFacts), new(observations.Records, capacity.MaxObservations));
        }
        catch (System.Data.Common.DbException) { throw new AuditStorageUnavailableException(false); }
    }, cancellationToken);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(capacity.WaitTimeoutMilliseconds);
        try { return await action(budget.Token).ConfigureAwait(false); }
        catch (Exception error) when (CapacityFailure(error) is { } exhausted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AuditStorageUnavailableException(exhausted);
        }
        catch (Exception) when (budget.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AuditStorageUnavailableException(false);
        }
    }

    public Task ConfigureTransactionAsync(CancellationToken cancellationToken)
    {
        var facts = capacity.MaxFacts.ToString(CultureInfo.InvariantCulture);
        var observations = capacity.MaxObservations.ToString(CultureInfo.InvariantCulture);
        var wait = capacity.WaitTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('nsn.auditing_max_facts', {facts}, true), set_config('nsn.auditing_max_observations', {observations}, true), set_config('lock_timeout', {wait}, true)",
            cancellationToken);
    }

    private static bool? CapacityFailure(Exception error)
    {
        var postgres = error as PostgresException ?? error.InnerException as PostgresException;
        if (postgres?.ConstraintName == "auditing_capacity_exhausted") { return true; }
        return postgres?.SqlState == PostgresErrorCodes.LockNotAvailable ? false : null;
    }

    private sealed record PoolRow(string Pool, long Records);
}
