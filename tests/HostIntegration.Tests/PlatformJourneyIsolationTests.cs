using System.Net;
using System.Net.Http.Json;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(PlatformJourneyDefinition.Name)]
public sealed class PlatformJourneyIsolationTests(PlatformJourneyTemplate databases)
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
    }
}
