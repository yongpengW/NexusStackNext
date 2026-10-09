using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Gateway;
using NexusStackNext.Identity.Application;
using NexusStackNext.IntegrationSupport;
using Xunit.Abstractions;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class IdentityQueryJourneyTests(JourneyDatabaseTemplates databases, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task ColdAndWarmQueries_ObserveCommittedGrantsAndImmediateSessionRevocationThroughTheGateway()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new MeasuredIdentityApp(database.ConnectionString);
        using var platform = app.CreateClient();
        using var artifacts = new JourneyFileStorage();
        Directory.CreateDirectory(artifacts.Root);
        var routePath = Path.Combine(artifacts.Root, "query-routes.json");
        var routes = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.json")))!;
        var cluster = Assert.Single(routes["clusters"]!.AsArray());
        Assert.Equal("platform-host", cluster!["clusterId"]!.GetValue<string>());
        var destinations = cluster["destinations"]!.AsArray();
        Assert.NotEmpty(destinations);
        foreach (var destination in destinations) { destination!["address"] = platform.BaseAddress!.ToString(); }
        await File.WriteAllTextAsync(routePath, routes.ToJsonString());
        await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath,
            new Dictionary<string, string> { ["Jwt__SigningKey"] = PersistentIdentityApp.SigningKey });

        var user = (await SendAsync(app, new CreateUserCommand("query-reader", "query-test-password"))).Value;
        var role = (await SendAsync(app, new CreateRoleCommand("query-reader", "Query reader"))).Value;
        var menu = await SendAsync(app, new CreateMenuCommand("Query reader", 1, null));
        Assert.True(menu.IsSuccess);
        Assert.True((await SendAsync(app, new AssignRoleCommand(user, role))).IsSuccess);
        using var timings = new IdentityQueryTimings(output);
        timings.Start();
        foreach (var phase in new[] { QueryPhase.EmptyCold, QueryPhase.EmptyWarm })
        {
            using var measurement = timings.Measure(phase);
            var permissions = await QueryAsync(app, new GetUserPermissionsQuery(user));
            Assert.True(permissions.IsSuccess);
            Assert.Empty(permissions.Value);
        }
        Assert.True((await SendAsync(app, new GrantMenuToRoleCommand(role, menu.Value.MenuId))).IsSuccess);
        Assert.True((await SendAsync(app, new CreateApiResourceCommand("/api/identity/users", "GET", menu.Value.MenuId))).IsSuccess);
        foreach (var phase in new[] { QueryPhase.GrantedCold, QueryPhase.GrantedWarm })
        {
            using var measurement = timings.Measure(phase);
            var permissions = await QueryAsync(app, new GetUserPermissionsQuery(user));
            Assert.True(permissions.IsSuccess);
            Assert.Equal("/api/identity/users:GET", Assert.Single(permissions.Value));
        }
        long acceptedVersion;
        using (timings.Measure(QueryPhase.SessionCurrent))
        {
            var current = await QueryAsync(app, new GetSessionVersionQuery(user));
            Assert.True(current.IsSuccess);
            acceptedVersion = current.Value;
        }

        Assert.True((await SendAsync(app, new CreateApiResourceCommand("/api/identity/menus", "GET", menu.Value.MenuId))).IsSuccess);
        await PlatformSettingsAccessTests.LoginAsync(gateway.Client, "query-reader", "query-test-password");
        foreach (var phase in new[] { QueryPhase.HttpCold, QueryPhase.HttpWarm })
        {
            using var measurement = timings.Measure(phase);
            using var read = await gateway.Client.GetAsync(new Uri("/api/identity/menus", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            await measurement.CompleteHttpAsync();
            Assert.True(measurement.Commands > 0, "HTTP trace must observe actual Identity database work.");
            Assert.True(measurement.JournalCommands > 0, "HTTP trace must observe actual source journal persistence.");
            Assert.True(measurement.ProviderCommands > 0, "HTTP trace must observe actual database provider execution.");
            Assert.True(measurement.SessionCommands > 0, "HTTP trace must observe the actual session authority database read.");
        }
        using var logout = await gateway.Client.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using (timings.Measure(QueryPhase.SessionAfterLogout))
        {
            var revoked = await QueryAsync(app, new GetSessionVersionQuery(user));
            Assert.True(revoked.IsSuccess);
            Assert.Equal(acceptedVersion + 1, revoked.Value);
        }
        using (var measurement = timings.Measure(QueryPhase.HttpRevoked))
        {
            using var denied = await gateway.Client.GetAsync(new Uri("/api/identity/menus", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            await measurement.CompleteHttpAsync();
            Assert.True(measurement.SessionCommands > 0, "Revocation measurement must observe the authoritative database.");
            Assert.True(measurement.JournalCommands > 0, "Rejected HTTP requests must still persist source observations.");
            Assert.True(measurement.ProviderCommands > 0, "Rejected HTTP measurements must observe actual database provider execution.");
        }
        Assert.True(timings.Commands > 0, "Query timing must observe actual Identity database work.");
    }

    private sealed class MeasuredIdentityApp(string connectionString) : PersistentIdentityApp(connectionString)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISessionStateReader));
                var access = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IAccessStateReader));
                Assert.NotNull(original.ImplementationFactory);
                Assert.NotNull(access.ImplementationFactory);
                services.Remove(original);
                services.Remove(access);
                services.AddSingleton(provider => new IdentityQueryTimings.AuthorityReader(
                    (ISessionStateReader)original.ImplementationFactory(provider), (IAccessStateReader)access.ImplementationFactory(provider)));
                services.AddSingleton<ISessionStateReader>(provider => provider.GetRequiredService<IdentityQueryTimings.AuthorityReader>());
                services.AddSingleton<IAccessStateReader>(provider => provider.GetRequiredService<IdentityQueryTimings.AuthorityReader>());
            });
        }
    }

    private static async Task<Result<T>> QueryAsync<T>(PersistentIdentityApp app, IQuery<T> query)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(query);
    }

    private static async Task<Result<T>> SendAsync<T>(PersistentIdentityApp app, ICommand<T> command)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(command);
    }

    private static async Task<Result> SendAsync(PersistentIdentityApp app, ICommand command)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(command);
    }
}
