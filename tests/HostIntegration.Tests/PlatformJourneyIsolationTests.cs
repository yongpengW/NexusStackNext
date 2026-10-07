using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PlatformJourneyIsolationTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task IndependentJourneys_DoNotShareUsersOrDatabaseFailures()
    {
        await using var first = await databases.CreateAsync();
        await using var second = await databases.CreateAsync();
        await using var firstHost = new PersistentIdentityApp(first.ConnectionString, schedulingWorkerEnabled: false);
        await using var secondHost = new PersistentIdentityApp(second.ConnectionString, schedulingWorkerEnabled: false);
        using var firstClient = firstHost.CreateClient();
        using var secondClient = secondHost.CreateClient();
        var firstStorage = firstHost.Services.GetRequiredService<IConfiguration>()["Files:StorageRoot"];
        var secondStorage = secondHost.Services.GetRequiredService<IConfiguration>()["Files:StorageRoot"];
        Assert.False(string.IsNullOrWhiteSpace(firstStorage));
        Assert.False(string.IsNullOrWhiteSpace(secondStorage));
        Assert.NotEqual(firstStorage, secondStorage);
        Assert.True(Directory.Exists(firstStorage));
        Assert.True(Directory.Exists(secondStorage));
        var account = new { userName = "independent-journey-user", password = "independent-journey-password" };
        using var created = await firstClient.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), account);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var duplicate = await firstClient.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), account);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var independent = await secondClient.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), account);
        Assert.Equal(HttpStatusCode.Created, independent.StatusCode);
        await first.SetAvailableAsync(false);
        using var unavailable = await firstClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        using var available = await secondClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        await firstHost.DisposeAsync();
        Assert.False(Directory.Exists(firstStorage));
        Assert.True(Directory.Exists(secondStorage));
        using var surviving = await secondClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
        await secondHost.DisposeAsync();
        Assert.False(Directory.Exists(secondStorage));
    }
}
