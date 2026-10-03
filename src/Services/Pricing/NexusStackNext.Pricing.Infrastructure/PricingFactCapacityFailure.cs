using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal static class PricingFactCapacityFailure
{
    internal static bool IsExhausted(DbUpdateException error)
        => error.InnerException is PostgresException { SqlState: "P0001", ConstraintName: "pricing_fact_capacity_exhausted" };
}
