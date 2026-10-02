using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingAuditTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Audit_PreservesCreationAndNoOps_AndRollsBackWithTheResultAndOutbox()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        var command = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        var clock = new MutableClock(createdAt);
        await using (var app = CreateApplication(clock, "creator"))
        await using (var scope = app.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(command)).IsSuccess);
            Assert.Equal(new EntityAuditMetadata(createdAt, "creator", null, null),
                (await sender.QueryAsync(new GetCostSheet(command.ItemId))).Value.Audit);
        }

        clock.Advance(TimeSpan.FromHours(1));
        CostSheetView edited;
        var update = command with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, PurchaseCost = 90m };
        await using (var app = CreateApplication(clock, "editor"))
        await using (var scope = app.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(update)).IsSuccess);
            edited = (await sender.QueryAsync(new GetCostSheet(command.ItemId))).Value;
            Assert.Equal(new EntityAuditMetadata(createdAt, "creator", clock.UtcNow, "editor"), edited.Audit);
            clock.Advance(TimeSpan.FromHours(1));
            Assert.True((await sender.SendAsync(update)).IsSuccess);
            Assert.True((await sender.SendAsync(update with { RequestId = Guid.NewGuid(), ExpectedVersion = edited.Version })).IsSuccess);
            Assert.Equal("costing.version_conflict", (await sender.SendAsync(update with { RequestId = Guid.NewGuid() })).Error.Code);
            Assert.Equal(edited, (await sender.QueryAsync(new GetCostSheet(command.ItemId))).Value);
        }

        clock.Advance(TimeSpan.FromHours(1));
        await using var worker = CreateApplication(clock, null);
        await using var workScope = worker.CreateAsyncScope();
        var work = workScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal(edited, (await work.QueryAsync(new GetCostSheet(command.ItemId))).Value);
        var superseded = (await work.SendAsync(new ClaimCostingWork())).Value!;
        Assert.True((await work.SendAsync(new CompleteCostingWork(superseded.TaskId, superseded.Epoch))).Value);
        Assert.Equal(edited, (await work.QueryAsync(new GetCostSheet(command.ItemId))).Value);
        var lease = (await work.SendAsync(new ClaimCostingWork())).Value!;
        await database.RejectOutboxWriteAsync(lease.TaskId, true);
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            work.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch)));
        Assert.Equal(edited, (await work.QueryAsync(new GetCostSheet(command.ItemId))).Value);
        Assert.Equal("Running", (await work.QueryAsync(new GetCostCalculation(lease.TaskId))).Value.State);
        Assert.True((await work.QueryAsync(new GetCostDelivery(lease.TaskId))).IsFailure);
        await database.RejectOutboxWriteAsync(lease.TaskId, false);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await work.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        var completed = (await work.QueryAsync(new GetCostSheet(command.ItemId))).Value;
        Assert.Equal(110m, completed.UnitCost);
        Assert.Equal(new EntityAuditMetadata(createdAt, "creator", clock.UtcNow, null), completed.Audit);

        clock.Advance(TimeSpan.FromHours(1));
        var unchanged = (await work.SendAsync(new ClaimCostingWork())).Value!;
        Assert.True((await work.SendAsync(new CompleteCostingWork(unchanged.TaskId, unchanged.Epoch))).Value);
        Assert.Equal(completed, (await work.QueryAsync(new GetCostSheet(command.ItemId))).Value);
    }

    [PostgresFact]
    public async Task HttpAudit_UsesAuthenticatedClaims_AndSurvivesWorkerProcessRestart()
    {
        var itemId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        DateTimeOffset createdAt;
        await using (var app = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString))
        {
            app.Authenticate();
            using var response = await app.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative),
                new { requestId, itemId, expectedVersion = 0, purchaseCost = 80m, freightCost = 20m, audit = new { createdBy = "forged" } });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var initial = (await app.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/costing/items/{itemId}", UriKind.Relative))).GetProperty("data").GetProperty("audit");
            createdAt = initial.GetProperty("createdAt").GetDateTimeOffset();
            Assert.NotEqual(default, createdAt);
            Assert.Equal("test-operator", initial.GetProperty("createdBy").GetString());
            Assert.Equal(JsonValueKind.Null, initial.GetProperty("updatedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, initial.GetProperty("updatedBy").ValueKind);
        }

        await using var restarted = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, worker: true);
        restarted.Authenticate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var task = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/costing/tasks/{requestId}", UriKind.Relative), timeout.Token);
            if (task.GetProperty("data").GetProperty("state").GetString() == "Succeeded") { break; }
            await Task.Delay(100, timeout.Token);
        }
        var completed = (await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/costing/items/{itemId}", UriKind.Relative))).GetProperty("data");
        Assert.Equal(100m, completed.GetProperty("unitCost").GetDecimal());
        var audit = completed.GetProperty("audit");
        Assert.Equal(createdAt, audit.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal("test-operator", audit.GetProperty("createdBy").GetString());
        Assert.True(audit.GetProperty("updatedAt").GetDateTimeOffset() >= createdAt);
        Assert.Equal(JsonValueKind.Null, audit.GetProperty("updatedBy").ValueKind);
    }

    private ServiceProvider CreateApplication(IClock clock, string? actor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddSingleton(clock);
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(actor));
        services.AddCostingPostgres(database.ConnectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
