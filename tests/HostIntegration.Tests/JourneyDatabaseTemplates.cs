using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.HostIntegration.Tests;

[CollectionDefinition(Name)]
public sealed class JourneyDatabaseDefinition : ICollectionFixture<JourneyDatabaseTemplates>
{
    public const string Name = "PostgreSQL journeys";
}

// The collection owns immutable, migration-only databases. Each caller still owns a fresh database.
// Lazy initialization avoids contacting PostgreSQL when all of a collection's cases are skipped.
public sealed class JourneyDatabaseTemplates : IAsyncLifetime
{
    private readonly Lazy<Task<IdentityJourneyDatabase>> _platform = new(() => CreateTemplateAsync(database => database.MigrateAsync()));
    private readonly Lazy<Task<IdentityJourneyDatabase>> _costing = new(() => CreateTemplateAsync(database => MigrateBusinessAsync(database, "costing")));
    private readonly Lazy<Task<IdentityJourneyDatabase>> _pricing = new(() => CreateTemplateAsync(database => MigrateBusinessAsync(database, "pricing")));

    public Task InitializeAsync() => Task.CompletedTask;

    private static async Task MigrateBusinessAsync(IdentityJourneyDatabase database, string context)
    {
        await using var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true);
        if (context == "costing") { await CostingDatabase.MigrateAsync(database.ConnectionString); }
        else { await PricingDatabase.MigrateAsync(database.ConnectionString); }
    }

    internal Task<IdentityJourneyDatabase> CreateAsync() => CreateAsync("platform");

    internal async Task<IdentityJourneyDatabase> CreateAsync(string context)
    {
        var template = context.ToLowerInvariant() switch
        {
            "platform" or "identity" or "files" or "scheduling" => _platform,
            "costing" => _costing,
            "pricing" => _pricing,
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, "No journey template exists for this context."),
        };
        return await (await template.Value).CopyAsync();
    }

    public async Task DisposeAsync()
    {
        List<Exception> failures = [];
        foreach (var template in new[] { _platform, _costing, _pricing })
        {
            if (template.IsValueCreated && template.Value.IsCompletedSuccessfully)
            {
                try { await (await template.Value).DisposeAsync(); }
                catch (Exception failure) { failures.Add(failure); }
            }
        }
        if (failures.Count != 0) { throw new AggregateException("Owned journey templates could not be cleaned up.", failures); }
    }

    private static async Task<IdentityJourneyDatabase> CreateTemplateAsync(Func<IdentityJourneyDatabase, Task> migrate)
    {
        var database = await IdentityJourneyDatabase.CreateAsync();
        try
        {
            await migrate(database);
            // Only this owned template is frozen. Other services' databases and pools are untouched.
            await database.SetAvailableAsync(false);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }
}
