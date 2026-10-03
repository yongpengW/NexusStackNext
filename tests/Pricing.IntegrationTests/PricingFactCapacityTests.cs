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
using NexusStackNext.Costing.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.Pricing.Infrastructure.Migrations;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed partial class PricingFactCapacityTests
{
    [PostgresFact]
    public async Task LastSlot_HasOneWinner_AndOnlyExpiredConfirmedFactsReleaseCapacity()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, 1);
            var clock = new MutableClock(DateTimeOffset.UtcNow);
            await using var app = CreateApplication(database.ConnectionString, clock);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var scope = app.CreateAsyncScope();
                var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(request);
                if (result.IsFailure) { Assert.Equal("pricing.audit_capacity_exhausted", result.Error.Code); }
                return (request, result.IsSuccess);
            }));
            var winner = Assert.Single(attempts, attempt => attempt.IsSuccess).request;
            Assert.Equal(3, attempts.Count(attempt => !attempt.IsSuccess));
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            foreach (var rejected in attempts.Where(attempt => !attempt.IsSuccess))
            {
                Assert.True((await sender.QueryAsync(new GetPriceQuote(rejected.request.ItemId))).IsFailure);
                Assert.True((await sender.QueryAsync(new GetRecalculation(rejected.request.RequestId))).IsFailure);
            }
            await SetQuotaAsync(database.ConnectionString, 2);
            var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
            Assert.Equal(winner.RequestId, lease.TaskId);
            Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var facts = await outbox.ReadPendingAsync(10, clock.UtcNow);
            Assert.Equal(2, facts.Count);
            foreach (var fact in facts) { await outbox.MarkDeliveredAsync(fact.Id, clock.UtcNow); }
            // 准备一个已确认且将过期的非事实消息。删除保护作为故障注入，防止清理器误删它。
            await ProtectConfirmedNonFactAsync(database.ConnectionString, clock.UtcNow);
            var update = winner with { RequestId = Guid.NewGuid(), ExpectedVersion = 2, Cost = 90m };
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(update)).Error.Code);
            await using var maintenance = app.CreateAsyncScope();
            var cleanup = maintenance.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("pricing");
            Assert.Equal(0, await cleanup.CleanupAsync());
            clock.UtcNow = clock.UtcNow.AddDays(8);
            Assert.Equal(2, await cleanup.CleanupAsync());
            Assert.Equal(100m, (await sender.QueryAsync(new GetPriceQuote(winner.ItemId))).Value.BreakEvenPrice);
            await SetQuotaAsync(database.ConnectionString, 1);
            Assert.True((await sender.SendAsync(update)).IsSuccess);
            Assert.Equal(0, await cleanup.CleanupAsync());
            var pending = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
            Assert.Equal(PriceQuoteCommittedV1.Name, pending.EventName);
            Assert.True(await outbox.MarkDeadLetteredAsync(pending.Id, "capacity-test", clock.UtcNow, 0));
            clock.UtcNow = clock.UtcNow.AddDays(8);
            Assert.Equal(0, await cleanup.CleanupAsync());
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(update with
            { RequestId = Guid.NewGuid(), ExpectedVersion = 3, Cost = 100m })).Error.Code);
            Assert.Equal(3, (await sender.QueryAsync(new GetPriceQuote(winner.ItemId))).Value.Version);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Upgrade_BackfillsUtf8Bytes_ExcludesNonFacts_AndDoesNotResetPolicy()
    {
        var database = new PricingDatabaseFixture { MigrateOnInitialize = false };
        await database.InitializeAsync();
        try
        {
            await LegacyMigrations.ApplyAsync(database.ConnectionString, "pricing", new InitialPricing(), new TaskExecutionOrigin(),
                new OutboxRetryRevision(), new CommittedFactCleanup(), new TaskManagement());
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
            await using var app = CreateApplication(database.ConnectionString, clock);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m))).IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var original = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
            var factBytes = Encoding.UTF8.GetByteCount(original.Payload);
            var unicodeId = Guid.NewGuid();
            var nonFactId = Guid.NewGuid();
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO pricing.outbox ("Id", "EventName", "Payload", "OccurredAt", "AttemptCount", "RetryRevision")
                    VALUES (@unicode, @fact, '中', @at, 0, 0), (@other, @event, @payload, @at, 0, 0)
                    """, connection);
                seed.Parameters.AddWithValue("unicode", unicodeId);
                seed.Parameters.AddWithValue("other", nonFactId);
                seed.Parameters.AddWithValue("fact", PriceQuoteCommittedV1.Name);
                seed.Parameters.AddWithValue("event", CostCalculatedV1.Name);
                seed.Parameters.AddWithValue("payload", new string('x', 100_000));
                seed.Parameters.AddWithValue("at", clock.UtcNow);
                Assert.Equal(2, await seed.ExecuteNonQueryAsync());
            }
            await PricingDatabase.MigrateAsync(database.ConnectionString);
            // '中' 占 3 个 UTF-8 字节。仅留 2 字节会拒绝；按字符或遗漏回填则会错误放行。
            await SetQuotaAsync(database.ConnectionString, 3, 2 * factBytes + 2, factBytes);
            await PricingDatabase.MigrateAsync(database.ConnectionString);
            var candidate = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(candidate)).Error.Code);
            Assert.True((await sender.QueryAsync(new GetPriceQuote(candidate.ItemId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(candidate.RequestId))).IsFailure);
            Assert.Equal(new[] { original.Id, unicodeId, nonFactId }.Order(),
                (await outbox.ReadPendingAsync(10, clock.UtcNow)).Select(entry => entry.Id).Order());
            await SetQuotaAsync(database.ConnectionString, 3, 2 * factBytes + 3, factBytes - 1);
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(candidate)).Error.Code);
            await SetQuotaAsync(database.ConnectionString, 3, 2 * factBytes + 3, factBytes);
            Assert.True((await sender.SendAsync(candidate)).IsSuccess);
            var retained = await outbox.ReadPendingAsync(10, clock.UtcNow);
            Assert.Equal(4, retained.Count);
            Assert.Equal(3, retained.Count(entry => entry.EventName == PriceQuoteCommittedV1.Name));
            Assert.Equal(100_000, Assert.Single(retained, entry => entry.Id == nonFactId).Payload.Length);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task FixtureReset_ReleasesDeletedFactUsage_AndPreservesItsQuota()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, 1);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            await database.ResetAsync();
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(request with
            { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() })).Error.Code);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task HostRestart_PreservesCapacity_AndWorkerUsesItsRetryBudgetBeforeManualRecovery()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, 1);
            var assembly = typeof(PricingHostMarker).Assembly.Location;
            var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            await using (var first = await BusinessProcess.StartAsync(assembly, "Pricing", database.ConnectionString))
            {
                first.Authenticate();
                using var accepted = await first.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), request);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }
            var settings = new Dictionary<string, string> { ["Pricing__Tasks__MaxAttempts"] = "1" };
            await using var restarted = await BusinessProcess.StartAsync(assembly, "Pricing", database.ConnectionString,
                worker: true, leaseDuration: TimeSpan.FromSeconds(60), settings: settings);
            restarted.Authenticate();
            var failed = await WaitForTaskAsync(restarted.Client, request.RequestId, "Failed");
            Assert.Equal(1, failed.GetProperty("attempts").GetInt32());
            Assert.Equal("Failed", Assert.Single(failed.GetProperty("history").EnumerateArray()).GetProperty("outcome").GetString());
            using var unchangedResponse = await restarted.Client.GetAsync(new Uri($"/api/pricing/items/{request.ItemId}", UriKind.Relative));
            var unchanged = (await unchangedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("1", unchanged.GetProperty("version").GetString());
            Assert.Equal(JsonValueKind.Null, unchanged.GetProperty("breakEvenPrice").ValueKind);
            using var refusedCost = await restarted.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
                request with { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refusedCost.StatusCode);
            Assert.Equal("pricing.audit_capacity_exhausted", (await refusedCost.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            using var refusedFee = await restarted.Client.PostAsJsonAsync(new Uri("/api/pricing/fee", UriKind.Relative),
                new UpdatePricingFee(Guid.NewGuid(), request.ItemId, 1, 0.3m));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refusedFee.StatusCode);
            Assert.Equal("pricing.audit_capacity_exhausted", (await refusedFee.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            var document = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
            foreach (var path in new[] { "/api/pricing/cost", "/api/pricing/fee" })
            {
                Assert.True(document.GetProperty("paths").GetProperty(path).GetProperty("post").GetProperty("responses").TryGetProperty("503", out _));
            }

            await SetQuotaAsync(database.ConnectionString, 2);
            using var retry = await restarted.Client.PostAsJsonAsync(new Uri($"/api/pricing/tasks/{request.RequestId}/retry", UriKind.Relative),
                new { expectedEpoch = failed.GetProperty("epoch").GetString() });
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            var recovered = await WaitForTaskAsync(restarted.Client, request.RequestId, "Succeeded");
            Assert.Equal(new[] { "Failed", "Succeeded" }, recovered.GetProperty("history").EnumerateArray().Select(attempt => attempt.GetProperty("outcome").GetString()));
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            Assert.Equal(100m, (await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
            Assert.Equal(2, (await scope.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ResultAtCapacity_RollsBackQuoteAndTaskCompletion_AndSameLeaseCanRecover()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, 1);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            var before = (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value;
            var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Error.Code);
            Assert.Equal(before, (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value);
            var running = (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value;
            Assert.Equal("Running", running.State);
            Assert.Equal(lease.Epoch, running.Epoch);
            Assert.Equal("Running", Assert.Single(running.History).Outcome);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));

            await SetQuotaAsync(database.ConnectionString, 2);
            Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
            var completed = (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value;
            Assert.Equal("Succeeded", completed.State);
            Assert.Equal("Succeeded", Assert.Single(completed.History).Outcome);
            var recovered = (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value;
            Assert.Equal(2, recovered.Version);
            Assert.Equal(1, recovered.CalculatedRevision);
            Assert.Equal(100m, recovered.BreakEvenPrice);
            Assert.False((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
            Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task PricingFeeAtCapacity_PreservesUpstreamCostAndQuote_AndSameScopeCanRecover()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, 2);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var item = Guid.NewGuid();
            Assert.True(await processor.HandleAsync(CostIngestionTests.Cost(item, 1, 100m)));
            var before = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
            var update = new UpdatePricingFee(Guid.NewGuid(), item, before.Version, 0.2m);
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(update)).Error.Code);
            Assert.Equal(before, (await sender.QueryAsync(new GetPriceQuote(item))).Value);
            Assert.True((await sender.QueryAsync(new GetRecalculation(update.RequestId))).IsFailure);
            Assert.True((await sender.SendAsync(update with { RequestId = Guid.NewGuid(), FeeRate = before.FeeRate })).IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);

            await SetQuotaAsync(database.ConnectionString, 3);
            Assert.True((await sender.SendAsync(update)).IsSuccess);
            var recovered = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
            Assert.Equal(before.Version + 1, recovered.Version);
            Assert.Equal(before.InputRevision + 1, recovered.InputRevision);
            Assert.Equal(0.2m, recovered.FeeRate);
            Assert.Equal(100m, recovered.Cost);
            Assert.Equal(1, recovered.CostingRevision);
            Assert.Equal("Pending", (await sender.QueryAsync(new GetRecalculation(update.RequestId))).Value.State);
            Assert.Equal(3, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task FirstCostMessageAtCapacity_RejectsItsWholeFactBatch_AndOriginalMessageCanRecoverOnce()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, 1);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var item = Guid.NewGuid();
            var message = CostIngestionTests.Cost(item, 3, 100m);
            Assert.False(await processor.HandleAsync(message));
            Assert.True((await sender.QueryAsync(new GetPriceQuote(item))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(message.MessageId))).IsFailure);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));

            await SetQuotaAsync(database.ConnectionString, 2);
            Assert.True(await processor.HandleAsync(message));
            var quote = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
            Assert.Equal(100m, quote.Cost);
            Assert.Equal(3, quote.CostingRevision);
            Assert.Equal("Pending", (await sender.QueryAsync(new GetRecalculation(message.MessageId))).Value.State);
            Assert.True(await processor.HandleAsync(message));
            Assert.Equal(quote, (await sender.QueryAsync(new GetPriceQuote(item))).Value);
            var serializer = new SystemTextJsonIntegrationEventSerializer();
            var facts = await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow);
            Assert.Equal(2, facts.Count);
            Assert.All(facts, fact => Assert.Equal(PriceQuoteCommittedV1.Name, fact.EventName));
            Assert.Equal(new[] { "costing-applied", "created" }, facts.Select(fact => serializer.Deserialize<PriceQuoteCommittedV1>(fact.Payload).Operation).Order());
            var older = CostIngestionTests.Cost(item, 1, 80m);
            var sameRevision = CostIngestionTests.Cost(item, 3, 100m);
            Assert.True(await processor.HandleAsync(older));
            Assert.True(await processor.HandleAsync(sameRevision));
            Assert.True((await sender.QueryAsync(new GetRecalculation(older.MessageId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(sameRevision.MessageId))).IsFailure);
            Assert.False(await processor.HandleAsync(CostIngestionTests.Cost(item, 3, 101m)));
            Assert.False(await processor.HandleAsync(CostIngestionTests.Cost(item, 4, 100m)));
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, quote.Version, 99m, 0m))).IsFailure);
            Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);
            var original = serializer.Deserialize<CostCalculatedV1>(message.Payload);
            Assert.False(await processor.HandleAsync(OutboxEntry.From(original with { UnitCost = 101m }, serializer).ToEnvelope()));
            Assert.Equal(quote, (await sender.QueryAsync(new GetPriceQuote(item))).Value);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ManualInputsAtCapacity_RejectQuoteAndTaskTogether_AndSameScopeCanRecover()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await SetQuotaAsync(database.ConnectionString, 1);
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var original = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            Assert.True((await sender.SendAsync(original)).IsSuccess);
            var before = (await sender.QueryAsync(new GetPriceQuote(original.ItemId))).Value;
            var update = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, Cost = 90m };
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(update)).Error.Code);
            Assert.Equal(before, (await sender.QueryAsync(new GetPriceQuote(original.ItemId))).Value);
            Assert.True((await sender.QueryAsync(new GetRecalculation(update.RequestId))).IsFailure);
            var rejectedCreation = original with { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() };
            Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(rejectedCreation)).Error.Code);
            Assert.True((await sender.QueryAsync(new GetPriceQuote(rejectedCreation.ItemId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(rejectedCreation.RequestId))).IsFailure);
            Assert.True((await sender.SendAsync(original)).IsSuccess);
            Assert.True((await sender.SendAsync(original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1 })).IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));

            await SetQuotaAsync(database.ConnectionString, 2);
            Assert.True((await sender.SendAsync(update)).IsSuccess);
            var recovered = (await sender.QueryAsync(new GetPriceQuote(original.ItemId))).Value;
            Assert.Equal(2, recovered.Version);
            Assert.Equal(2, recovered.InputRevision);
            Assert.Equal(90m, recovered.Cost);
            Assert.Equal("Pending", (await sender.QueryAsync(new GetRecalculation(update.RequestId))).Value.State);
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
        services.AddPricingPostgres(connection, new PricingTaskOptions { LeaseDuration = TimeSpan.FromSeconds(60) });
        services.AddPricingFactCleanup(new CommittedFactCleanupOptions { Enabled = false });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task SetQuotaAsync(string connectionString, long maxRecords, long maxPayloadBytes = 268435456, int maxRecordPayloadBytes = 16384)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE pricing.fact_capacity SET "MaxRecords" = @records, "MaxPayloadBytes" = @total, "MaxRecordPayloadBytes" = @single
            """, connection);
        command.Parameters.AddWithValue("records", maxRecords);
        command.Parameters.AddWithValue("total", maxPayloadBytes);
        command.Parameters.AddWithValue("single", maxRecordPayloadBytes);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task ProtectConfirmedNonFactAsync(string connectionString, DateTimeOffset at)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        await using var seed = new NpgsqlCommand("""
            INSERT INTO pricing.outbox ("Id", "EventName", "Payload", "OccurredAt", "DeliveredAt", "AttemptCount", "RetryRevision")
            VALUES (@id, @event, '{}', @at, @at, 0, 0);
            CREATE FUNCTION pricing.reject_nonfact_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF OLD."EventName" <> 'pricing.price-quote-committed.v1' THEN
                RAISE EXCEPTION 'nonfact deletion prohibited by test fixture'
                  USING ERRCODE = 'P0001', CONSTRAINT = 'capacity_test_nonfact_delete';
              END IF;
              RETURN OLD;
            END $$;
            CREATE TRIGGER reject_nonfact_cleanup BEFORE DELETE ON pricing.outbox
              FOR EACH ROW EXECUTE FUNCTION pricing.reject_nonfact_cleanup();
            """, connection);
        seed.Parameters.AddWithValue("id", id);
        seed.Parameters.AddWithValue("event", CostCalculatedV1.Name);
        seed.Parameters.AddWithValue("at", at);
        await seed.ExecuteNonQueryAsync();
        // 反向验证准备的保护确实看得见该行，而不是不存在的记录导致静默成功。
        await using var probe = new NpgsqlCommand("DELETE FROM pricing.outbox WHERE \"Id\" = @id", connection);
        probe.Parameters.AddWithValue("id", id);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => probe.ExecuteNonQueryAsync());
        Assert.Equal("capacity_test_nonfact_delete", refused.ConstraintName);
    }

    private static async Task<JsonElement> WaitForTaskAsync(HttpClient client, Guid taskId, string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            using var response = await client.GetAsync(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var task = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            if (task.GetProperty("state").GetString() == state) { return task; }
            await Task.Delay(100, timeout.Token);
        }
    }
}
