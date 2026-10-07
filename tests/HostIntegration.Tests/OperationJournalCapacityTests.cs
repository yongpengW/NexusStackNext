using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class OperationJournalCapacityTests(JourneyDatabaseTemplates databases)
{
    [Theory]
    [InlineData("MaxRecords", "0")]
    [InlineData("MaxRecords", "10000001")]
    [InlineData("MaxPayloadBytes", "0")]
    [InlineData("MaxPayloadBytes", "68719476737")]
    [InlineData("MaxRecordPayloadBytes", "0")]
    [InlineData("MaxRecordPayloadBytes", "65537")]
    public void InvalidCapacityConfiguration_IsRejectedAtComposition(string key, string value)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
            ["OperationJournal:Capacity:" + key] = value,
        });
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "probe"));
    }

    [PostgresFact]
    public async Task ConcurrentWriters_ShareTheSameRecordAndByteBudget_AfterReopen()
    {
        foreach (var limitRecords in new[] { true, false })
        {
            await using var database = await databases.CreateAsync("journal");
            var sample = Started();
            var wireSize = Encoding.UTF8.GetByteCount(new SystemTextJsonIntegrationEventSerializer().Serialize(sample));
            var capacity = new OperationJournalCapacityOptions
            {
                MaxRecords = limitRecords ? 2 : 100,
                MaxPayloadBytes = limitRecords ? 1_000_000 : wireSize * 2,
            };
            await using (var app = PostgresApplication(database.ConnectionString, capacity))
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writers = Enumerable.Range(0, 6).Select(async _ =>
                {
                    await using var scope = app.CreateAsyncScope();
                    await release.Task;
                    return await scope.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(Started());
                }).ToArray();
                release.SetResult();
                var results = await Task.WhenAll(writers);
                Assert.Equal(2, results.Count(result => result.IsSuccess));
                Assert.Equal(4, results.Count(result => result.IsFailure && result.Error.Code == "operation_journal.capacity_exceeded"));
            }
            await using var reopened = PostgresApplication(database.ConnectionString, capacity);
            await using var read = reopened.CreateAsyncScope();
            var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
                .ReadPendingAsync(10, DateTimeOffset.MaxValue);
            Assert.Equal(2, pending.Count);
            Assert.Equal("operation_journal.capacity_exceeded",
                (await read.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(Started())).Error.Code);
            var health = await reopened.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Name == "operation-journal");
            Assert.Equal(2L, health.Entries["operation-journal"].Data["retainedRecords"]);
            Assert.Equal((long)wireSize * 2, health.Entries["operation-journal"].Data["retainedPayloadBytes"]);
        }
    }

    [PostgresFact]
    public async Task Migration_CountsPreviouslyDeliveredRecords_WithoutChangingTheirContent()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<OperationJournalDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, OperationJournalDbContext.SchemaName).Options;
        await using (var context = new OperationJournalDbContext(options))
        {
            await context.GetService<IMigrator>().MigrateAsync("20261002171202_InitialOperationJournal");
        }
        var legacy = Started();
        var payload = new SystemTextJsonIntegrationEventSerializer().Serialize(legacy);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO operation_journal.outbox
                ("Id", "EventName", "Payload", "OccurredAt", "AttemptCount", "DeliveredAt", "OperationId", "Phase", "Source")
                VALUES (@id, 'auditing.operation-observed.v1', @payload, @at, 0, @at, @operation, 'started', 'pricing');
                """, connection);
            seed.Parameters.AddWithValue("id", legacy.EventId);
            seed.Parameters.AddWithValue("payload", payload);
            seed.Parameters.AddWithValue("at", legacy.OccurredAt);
            seed.Parameters.AddWithValue("operation", legacy.OperationId);
            await seed.ExecuteNonQueryAsync();
        }
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        foreach (var limitRecords in new[] { true, false })
        {
            await using var app = PostgresApplication(database.ConnectionString, new()
            {
                MaxRecords = limitRecords ? 1 : 100,
                MaxPayloadBytes = limitRecords ? 1_000_000 : Encoding.UTF8.GetByteCount(payload),
            });
            await using var scope = app.CreateAsyncScope();
            var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
            Assert.True((await journal.AppendAsync(legacy)).IsSuccess);
            Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started())).Error.Code);
            Assert.Equal("operation_journal.identity_conflict", (await journal.AppendAsync(legacy with { ActorId = "changed" })).Error.Code);
        }
    }

    private static ServiceProvider PostgresApplication(string connection, OperationJournalCapacityOptions capacity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalPostgresStorage(connection, capacity);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task FullJournal_LeavesBusinessResponsesIntact_AndReportsMissingObservations()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
            ["OperationJournal:Capacity:MaxRecords"] = "1",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "probe");
        await using var app = builder.Build();
        app.UseRouting();
        app.UseOperationJournal();
        var effects = 0;
        app.MapPost("/work", () => { effects++; return Results.Accepted(); });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        for (var index = 0; index < 2; index++)
        {
            using var response = await client.PostAsync("/work", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        Assert.Equal(2, effects);
        Assert.Single(await app.Services.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal(3, app.Services.GetRequiredService<OperationJournalStatus>().FailureCount);
        var health = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Name == "operation-journal");
        Assert.Equal(HealthStatus.Degraded, health.Status);
    }

    [PostgresFact]
    public async Task PostgresPayloadBudget_UsesWireBytesAndRejectedAppendDoesNotReserveIdentity()
    {
        foreach (var singleRecordLimit in new[] { true, false })
        {
            await using var database = await databases.CreateAsync("journal");
            var first = Started();
            var wireSize = Encoding.UTF8.GetByteCount(new SystemTextJsonIntegrationEventSerializer().Serialize(first));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOperationJournalPostgresStorage(database.ConnectionString, new()
            {
                MaxPayloadBytes = wireSize * 2,
                MaxRecordPayloadBytes = singleRecordLimit ? wireSize : 65_536,
            });
            await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            await using var scope = app.CreateAsyncScope();
            var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
            Assert.True((await journal.AppendAsync(first)).IsSuccess);
            Assert.True((await journal.AppendAsync(first)).IsSuccess);
            var second = Started();
            Assert.Equal(singleRecordLimit ? "operation_journal.payload_too_large" : "operation_journal.capacity_exceeded",
                (await journal.AppendAsync(second with { ActorId = "操作人员" })).Error.Code);
            Assert.True((await journal.AppendAsync(second)).IsSuccess);
            Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started())).Error.Code);
            Assert.Equal(2, (await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
                .ReadPendingAsync(10, DateTimeOffset.MaxValue)).Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MemoryPayloadBudget_UsesWireBytesAndRejectedAppendDoesNotReserveIdentity(bool singleRecordLimit)
    {
        var first = Started();
        var wireSize = Encoding.UTF8.GetByteCount(new SystemTextJsonIntegrationEventSerializer().Serialize(first));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalMemoryStorage(new()
        {
            MaxPayloadBytes = wireSize * 2,
            MaxRecordPayloadBytes = singleRecordLimit ? wireSize : 65_536,
        });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var journal = app.GetRequiredService<IOperationJournal>();
        Assert.True((await journal.AppendAsync(first)).IsSuccess);
        Assert.True((await journal.AppendAsync(first)).IsSuccess);
        var second = Started();
        Assert.Equal(singleRecordLimit ? "operation_journal.payload_too_large" : "operation_journal.capacity_exceeded",
            (await journal.AppendAsync(second with { ActorId = "操作人员" })).Error.Code);
        Assert.True((await journal.AppendAsync(second)).IsSuccess);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started())).Error.Code);
        Assert.Equal(2, (await app.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(10, DateTimeOffset.MaxValue)).Count);
    }

    [PostgresFact]
    public async Task PostgresCapacity_RejectsNewRecordsButPreservesDuplicatesAndAcceptedRecords()
    {
        await using var database = await databases.CreateAsync("journal");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalPostgresStorage(database.ConnectionString, new() { MaxRecords = 2 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await AssertRecordLimitAsync(app);
    }

    [Fact]
    public async Task MemoryCapacity_RejectsNewRecordsButPreservesDuplicatesAndAcceptedRecords()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationJournalMemoryStorage(new() { MaxRecords = 2 });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await AssertRecordLimitAsync(app);
    }

    private static async Task AssertRecordLimitAsync(ServiceProvider app)
    {
        await using var scope = app.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var first = Started();
        Assert.True((await journal.AppendAsync(first)).IsSuccess);
        Assert.True((await journal.AppendAsync(first)).IsSuccess);
        Assert.Equal("operation_journal.identity_conflict", (await journal.AppendAsync(first with { HttpMethod = "PUT" })).Error.Code);
        var second = Started();
        Assert.True((await journal.AppendAsync(second)).IsSuccess);
        var health = await app.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Name == "operation-journal");
        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Equal(2L, health.Entries["operation-journal"].Data["retainedRecords"]);
        Assert.Equal(0L, health.Entries["operation-journal"].Data["failedWrites"]);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started())).Error.Code);
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Count);
        Assert.True((await journal.AppendAsync(first)).IsSuccess);
        Assert.Equal("operation_journal.identity_conflict", (await journal.AppendAsync(first with { EventId = Guid.NewGuid() })).Error.Code);
        await outbox.MarkDeliveredAsync(first.EventId, DateTimeOffset.UtcNow);
        await outbox.MarkDeadLetteredAsync(second.EventId, "stopped", DateTimeOffset.UtcNow, 0);
        Assert.Equal("operation_journal.capacity_exceeded", (await journal.AppendAsync(Started())).Error.Code);
        Assert.True((await journal.AppendAsync(first)).IsSuccess);
        Assert.True((await journal.AppendAsync(second)).IsSuccess);
    }

    private static OperationObservedV1 Started() => new()
    {
        EventId = Guid.NewGuid(),
        OperationId = Guid.NewGuid(),
        Source = "pricing",
        Kind = "http",
        Phase = "started",
        TraceId = "capacity-test",
        HttpMethod = "POST",
        RouteTemplate = "/api/pricing/cost",
        OccurredAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
    };
}
