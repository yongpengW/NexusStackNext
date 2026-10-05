using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal static class PricingRequestTransaction
{
    public static async Task<IDbContextTransaction> BeginAsync(PricingDbContext database, Guid requestId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        try
        {
            return await BeginAndLockAsync(database, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException error) when (error.IsTransient && connection.State is ConnectionState.Broken or ConnectionState.Closed
            && !cancellationToken.IsCancellationRequested)
        {
            // Only BEGIN and the request advisory lock have been attempted: no business writes
            // or commit can have happened. Discard this pool's dead connections and try once.
            NpgsqlConnection.ClearPool(connection);
            await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            return await BeginAndLockAsync(database, requestId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IDbContextTransaction> BeginAndLockAsync(PricingDbContext database, Guid requestId, CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Npgsql defers BEGIN until the first command. The lock also verifies the rented
            // physical connection without adding a round trip to every healthy request.
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-request/" + requestId}, 0))", cancellationToken).ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
