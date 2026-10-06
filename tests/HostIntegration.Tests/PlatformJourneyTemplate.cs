namespace NexusStackNext.HostIntegration.Tests;

[CollectionDefinition(Name)]
public sealed class PlatformJourneyDefinition : ICollectionFixture<PlatformJourneyTemplate>
{
    public const string Name = "Platform PostgreSQL journeys";
}

// The collection owns one immutable, migration-only database. Each caller still owns a fresh database.
// Lazy initialization avoids contacting PostgreSQL when all of a collection's cases are skipped.
public sealed class PlatformJourneyTemplate : IAsyncLifetime
{
    private readonly Lazy<Task<IdentityJourneyDatabase>> _template = new(CreateTemplateAsync);

    public Task InitializeAsync() => Task.CompletedTask;

    internal async Task<IdentityJourneyDatabase> CreateAsync()
        => await (await _template.Value).CopyAsync();

    public async Task DisposeAsync()
    {
        if (_template.IsValueCreated && _template.Value.IsCompletedSuccessfully)
        {
            await (await _template.Value).DisposeAsync();
        }
    }

    private static async Task<IdentityJourneyDatabase> CreateTemplateAsync()
    {
        var database = await IdentityJourneyDatabase.CreateAsync();
        try
        {
            await database.MigrateAsync();
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
