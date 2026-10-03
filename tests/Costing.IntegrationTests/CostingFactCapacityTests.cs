using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Costing.Infrastructure.Migrations;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingFactCapacityTests
{
    [PostgresFact]
    public async Task FullFactCapacity_DoesNotRejectScheduledAcceptanceOfAnUnchangedCostSnapshot()
    {
        var database = new CostingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, maxRecords: 1);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(ScheduleTriggeredV1.Name);
            var now = DateTimeOffset.UtcNow;
            var message = new ScheduleTriggeredV1
            {
                PlanId = 101,
                TriggerSequence = 1,
                TargetKind = CostingScheduleTarget.Recalculate,
                TargetId = request.ItemId,
                CreatedBy = "42",
                ScheduledAt = now,
                OccurredAt = now,
            };
            var envelope = OutboxEntry.From(message, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope();
            Assert.True(await processor.HandleAsync(envelope));
            var receipt = (await sender.QueryAsync(new GetScheduledCostReceipt(message.EventId))).Value;
            Assert.Equal("Accepted", receipt.Decision);
            Assert.Equal(message.EventId, receipt.TaskId);
            var task = (await sender.QueryAsync(new GetCostCalculation(message.EventId))).Value;
            Assert.Equal("Pending", task.State);
            Assert.Equal(1, task.InputRevision);
            Assert.True(await processor.HandleAsync(envelope));
            Assert.Equal(receipt, (await sender.QueryAsync(new GetScheduledCostReceipt(message.EventId))).Value);
            Assert.Equal(1, (await sender.QueryAsync(new GetCostSheet(request.ItemId))).Value.Version);
            Assert.Single(await scope.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(10, DateTimeOffset.UtcNow));
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task LastSlot_HasOneWinner_AndOnlyExpiredConfirmedFactCleanupReleasesIt()
    {
        var database = new CostingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, maxRecords: 1);
            var clock = new MutableClock(DateTimeOffset.UtcNow);
            await using var app = CreateApplication(database.ConnectionString, clock);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var attempt = app.CreateAsyncScope();
                var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
                var result = await attempt.ServiceProvider.GetRequiredService<ISender>().SendAsync(request);
                if (result.IsFailure) { Assert.Equal("costing.audit_capacity_exhausted", result.Error.Code); }
                return (request, result.IsSuccess);
            }));
            var winner = Assert.Single(attempts, attempt => attempt.IsSuccess).request;
            Assert.Equal(3, attempts.Count(attempt => !attempt.IsSuccess));
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            foreach (var rejected in attempts.Where(attempt => !attempt.IsSuccess))
            {
                Assert.True((await sender.QueryAsync(new GetCostSheet(rejected.request.ItemId))).IsFailure);
                Assert.True((await sender.QueryAsync(new GetCostCalculation(rejected.request.RequestId))).IsFailure);
            }
            await SetQuotaAsync(database.ConnectionString, maxRecords: 2);
            var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
            Assert.Equal(winner.RequestId, lease.TaskId);
            Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var entries = await outbox.ReadPendingAsync(10, clock.UtcNow);
            Assert.Equal(3, entries.Count);
            foreach (var entry in entries) { await outbox.MarkDeliveredAsync(entry.Id, clock.UtcNow); }
            var update = winner with { RequestId = Guid.NewGuid(), ExpectedVersion = 2, PurchaseCost = 90m };
            Assert.Equal("costing.audit_capacity_exhausted", (await sender.SendAsync(update)).Error.Code);
            await using var maintenance = app.CreateAsyncScope();
            var cleanup = maintenance.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("costing");
            Assert.Equal(0, await cleanup.CleanupAsync());
            clock.UtcNow = clock.UtcNow.AddDays(8);
            Assert.Equal(2, await cleanup.CleanupAsync());
            Assert.Equal("Delivered", (await sender.QueryAsync(new GetCostDelivery(winner.RequestId))).Value.State);
            await SetQuotaAsync(database.ConnectionString, maxRecords: 1);
            Assert.True((await sender.SendAsync(update)).IsSuccess);
            Assert.Equal(0, await cleanup.CleanupAsync());
            var pending = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
            Assert.Equal(CostSheetCommittedV1.Name, pending.EventName);
            Assert.True(await outbox.MarkDeadLetteredAsync(pending.Id, "capacity-test", clock.UtcNow, 0));
            clock.UtcNow = clock.UtcNow.AddDays(8);
            Assert.Equal(0, await cleanup.CleanupAsync());
            Assert.Equal("costing.audit_capacity_exhausted", (await sender.SendAsync(update with
            { RequestId = Guid.NewGuid(), ExpectedVersion = 3, PurchaseCost = 100m })).Error.Code);
            Assert.Equal(3, (await sender.QueryAsync(new GetCostSheet(winner.ItemId))).Value.Version);
            Assert.Equal("Delivered", (await sender.QueryAsync(new GetCostDelivery(winner.RequestId))).Value.State);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Upgrade_BackfillsUtf8Bytes_ExcludesBusinessMessages_AndDoesNotResetPolicy()
    {
        var database = new CostingDatabaseFixture { MigrateOnInitialize = false };
        await database.InitializeAsync();
        try
        {
            await LegacyMigrations.ApplyAsync(database.ConnectionString, "costing", new InitialCosting(), new TaskExecutionOrigin(),
                new OutboxRetryRevision(), new CommittedFactCleanup(), new TaskManagement());
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
            await using var app = CreateApplication(database.ConnectionString, clock);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m))).IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var original = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
            var factBytes = Encoding.UTF8.GetByteCount(original.Payload);
            var unicodeId = Guid.NewGuid();
            var businessId = Guid.NewGuid();
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO costing.outbox ("Id", "EventName", "Payload", "OccurredAt", "AttemptCount", "RetryRevision")
                    VALUES (@unicode, @fact, '中', @at, 0, 0), (@business, @event, @payload, @at, 0, 0)
                    """, connection);
                seed.Parameters.AddWithValue("unicode", unicodeId);
                seed.Parameters.AddWithValue("business", businessId);
                seed.Parameters.AddWithValue("fact", CostSheetCommittedV1.Name);
                seed.Parameters.AddWithValue("event", CostCalculatedV1.Name);
                seed.Parameters.AddWithValue("payload", new string('x', 100_000));
                seed.Parameters.AddWithValue("at", clock.UtcNow);
                Assert.Equal(2, await seed.ExecuteNonQueryAsync());
            }
            await CostingDatabase.MigrateAsync(database.ConnectionString);
            // '中' 占 3 个 UTF-8 字节。这里故意只为它留 2 字节：按字符计数或遗忘回填会错误放行。
            await SetQuotaAsync(database.ConnectionString, maxRecords: 3, maxPayloadBytes: 2 * factBytes + 2, maxRecordPayloadBytes: factBytes);
            await CostingDatabase.MigrateAsync(database.ConnectionString);
            var candidate = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            Assert.Equal("costing.audit_capacity_exhausted", (await sender.SendAsync(candidate)).Error.Code);
            Assert.True((await sender.QueryAsync(new GetCostSheet(candidate.ItemId))).IsFailure);
            Assert.Equal(new[] { original.Id, unicodeId, businessId }.Order(),
                (await outbox.ReadPendingAsync(10, clock.UtcNow)).Select(entry => entry.Id).Order());
            await SetQuotaAsync(database.ConnectionString, maxRecords: 3, maxPayloadBytes: 2 * factBytes + 3, maxRecordPayloadBytes: factBytes - 1);
            Assert.Equal("costing.audit_capacity_exhausted", (await sender.SendAsync(candidate)).Error.Code);
            await SetQuotaAsync(database.ConnectionString, maxRecords: 3, maxPayloadBytes: 2 * factBytes + 3, maxRecordPayloadBytes: factBytes);
            Assert.True((await sender.SendAsync(candidate)).IsSuccess);
            var retained = await outbox.ReadPendingAsync(10, clock.UtcNow);
            Assert.Equal(4, retained.Count);
            Assert.Equal(3, retained.Count(entry => entry.EventName == CostSheetCommittedV1.Name));
            Assert.Equal(100_000, Assert.Single(retained, entry => entry.Id == businessId).Payload.Length);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task FixtureReset_ReleasesDeletedFactUsage_AndPreservesItsQuota()
    {
        var database = new CostingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, maxRecords: 1);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            await database.ResetAsync();
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            Assert.Equal("costing.audit_capacity_exhausted", (await sender.SendAsync(request with
            { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() })).Error.Code);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task HostRestart_PreservesCapacity_AndWorkerUsesItsRetryBudgetBeforeManualRecovery()
    {
        var database = new CostingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, maxRecords: 1);
            var assembly = typeof(CostingHostMarker).Assembly.Location;
            var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            await using (var first = await BusinessProcess.StartAsync(assembly, "Costing", database.ConnectionString))
            {
                first.Authenticate();
                using var accepted = await first.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), request);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }
            var settings = new Dictionary<string, string> { ["Costing__Tasks__MaxAttempts"] = "1" };
            await using var restarted = await BusinessProcess.StartAsync(assembly, "Costing", database.ConnectionString,
                worker: true, leaseDuration: TimeSpan.FromSeconds(60), settings: settings);
            restarted.Authenticate();
            var failed = await WaitForTaskAsync(restarted.Client, request.RequestId, "Failed");
            Assert.Equal(1, failed.GetProperty("attempts").GetInt32());
            Assert.Equal("Failed", Assert.Single(failed.GetProperty("history").EnumerateArray()).GetProperty("outcome").GetString());
            using var unchangedResponse = await restarted.Client.GetAsync(new Uri($"/api/costing/items/{request.ItemId}", UriKind.Relative));
            var unchanged = (await unchangedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("1", unchanged.GetProperty("version").GetString());
            Assert.Equal(JsonValueKind.Null, unchanged.GetProperty("unitCost").ValueKind);
            using var delivery = await restarted.Client.GetAsync(new Uri($"/api/costing/tasks/{request.RequestId}/delivery", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, delivery.StatusCode);
            using var refused = await restarted.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative),
                request with { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("costing.audit_capacity_exhausted", problem.GetProperty("errorCode").GetString());
            var document = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
            Assert.True(document.GetProperty("paths").GetProperty("/api/costing/cost").GetProperty("post")
                .GetProperty("responses").TryGetProperty("503", out _));

            await SetQuotaAsync(database.ConnectionString, maxRecords: 2);
            using var retry = await restarted.Client.PostAsJsonAsync(new Uri($"/api/costing/tasks/{request.RequestId}/retry", UriKind.Relative),
                new { expectedEpoch = failed.GetProperty("epoch").GetString() });
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            var recovered = await WaitForTaskAsync(restarted.Client, request.RequestId, "Succeeded");
            Assert.Equal(new[] { "Failed", "Succeeded" }, recovered.GetProperty("history").EnumerateArray()
                .Select(attempt => attempt.GetProperty("outcome").GetString()));
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.Equal(100m, (await sender.QueryAsync(new GetCostSheet(request.ItemId))).Value.UnitCost);
            var entries = await scope.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(10, DateTimeOffset.UtcNow);
            Assert.Equal(2, entries.Count(entry => entry.EventName == CostSheetCommittedV1.Name));
            Assert.Equal(request.RequestId, Assert.Single(entries, entry => entry.EventName == CostCalculatedV1.Name).Id);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ResultAtCapacity_RollsBackCalculationCompletionAndDelivery_ThenSameLeaseCanRecover()
    {
        var database = new CostingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, maxRecords: 1);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
            var refused = await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch));
            Assert.Equal("costing.audit_capacity_exhausted", refused.Error.Code);
            var unchanged = (await sender.QueryAsync(new GetCostSheet(request.ItemId))).Value;
            Assert.Equal(1, unchanged.Version);
            Assert.Equal(0, unchanged.CalculatedRevision);
            Assert.Null(unchanged.UnitCost);
            var running = (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Value;
            Assert.Equal("Running", running.State);
            Assert.Equal(lease.Epoch, running.Epoch);
            Assert.Equal("Running", Assert.Single(running.History).Outcome);
            Assert.True((await sender.QueryAsync(new GetCostDelivery(request.RequestId))).IsFailure);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));

            await SetQuotaAsync(database.ConnectionString, maxRecords: 2);
            Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
            var completed = (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Value;
            Assert.Equal("Succeeded", completed.State);
            Assert.Equal("Succeeded", Assert.Single(completed.History).Outcome);
            var result = (await sender.QueryAsync(new GetCostSheet(request.ItemId))).Value;
            Assert.Equal(2, result.Version);
            Assert.Equal(1, result.CalculatedRevision);
            Assert.Equal(100m, result.UnitCost);
            Assert.False((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
            var entries = await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow);
            Assert.Equal(2, entries.Count(entry => entry.EventName == CostSheetCommittedV1.Name));
            Assert.Equal(request.RequestId, Assert.Single(entries, entry => entry.EventName == CostCalculatedV1.Name).Id);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task InputsAtCapacity_RejectStateAndTaskTogether_AndSameScopeCanRecover()
    {
        var database = new CostingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, maxRecords: 1);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var original = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            Assert.True((await sender.SendAsync(original)).IsSuccess);
            var update = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, PurchaseCost = 90m };
            Assert.Equal("costing.audit_capacity_exhausted", (await sender.SendAsync(update)).Error.Code);
            var unchanged = (await sender.QueryAsync(new GetCostSheet(original.ItemId))).Value;
            Assert.Equal(1, unchanged.Version);
            Assert.Equal(1, unchanged.InputRevision);
            Assert.Equal(80m, unchanged.PurchaseCost);
            Assert.True((await sender.QueryAsync(new GetCostCalculation(update.RequestId))).IsFailure);
            var rejectedCreation = original with { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() };
            Assert.Equal("costing.audit_capacity_exhausted", (await sender.SendAsync(rejectedCreation)).Error.Code);
            Assert.True((await sender.QueryAsync(new GetCostSheet(rejectedCreation.ItemId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetCostCalculation(rejectedCreation.RequestId))).IsFailure);
            Assert.True((await sender.SendAsync(original)).IsSuccess);
            Assert.True((await sender.SendAsync(original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1 })).IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            Assert.Equal(CostSheetCommittedV1.Name, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).EventName);

            await SetQuotaAsync(database.ConnectionString, maxRecords: 2);
            Assert.True((await sender.SendAsync(update)).IsSuccess);
            var recovered = (await sender.QueryAsync(new GetCostSheet(original.ItemId))).Value;
            Assert.Equal(2, recovered.Version);
            Assert.Equal(2, recovered.InputRevision);
            Assert.Equal(90m, recovered.PurchaseCost);
            Assert.Equal("Pending", (await sender.QueryAsync(new GetCostCalculation(update.RequestId))).Value.State);
            Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
        }
        finally { await database.DisposeAsync(); }
    }

    private static ServiceProvider CreateApplication(string connection, IClock? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        if (clock is not null) { services.AddSingleton(clock); }
        services.AddCostingPostgres(connection, new CostingTaskOptions { LeaseDuration = TimeSpan.FromSeconds(60) });
        services.AddCostingFactCleanup(new CommittedFactCleanupOptions { Enabled = false });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task SetQuotaAsync(string connectionString, long maxRecords, long maxPayloadBytes = 268435456, int maxRecordPayloadBytes = 16384)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE costing.fact_capacity SET "MaxRecords" = @records, "MaxPayloadBytes" = @total, "MaxRecordPayloadBytes" = @single
            """, connection);
        command.Parameters.AddWithValue("records", maxRecords);
        command.Parameters.AddWithValue("total", maxPayloadBytes);
        command.Parameters.AddWithValue("single", maxRecordPayloadBytes);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<JsonElement> WaitForTaskAsync(HttpClient client, Guid taskId, string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            using var response = await client.GetAsync(new Uri($"/api/costing/tasks/{taskId}", UriKind.Relative), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var task = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            if (task.GetProperty("state").GetString() == state) { return task; }
            await Task.Delay(100, timeout.Token);
        }
    }
}
