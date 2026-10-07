using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationCompatibilityTests
{
    // 来自 #61 的独立 Release 编译产物；不是用本次序列化器计算出来的期望值。
    private const string LegacyPayload = """
        {"eventName":"auditing.operation-observed.v1","operationId":"22222222-2222-2222-2222-222222222222","source":"legacy-source","kind":"http","phase":"finished","outcome":"completed","actorId":null,"traceId":"legacy-trace","httpMethod":"PUT","routeTemplate":"/api/legacy/{id}","statusCode":204,"durationMs":25,"eventId":"11111111-1111-1111-1111-111111111111","occurredAt":"2026-10-03T00:00:00+00:00"}
        """;
    private const string LegacyHash = "6ACCB761DFD869174C401655E0261433DC5CD20B0E2468C6EB0949A12B1BDB59";

    [Fact]
    public void MessageWithoutMetadata_KeepsFrozenV1PayloadByteForByte()
    {
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var message = serializer.Deserialize<OperationObservedV1>(LegacyPayload);
        Assert.Null(message.Metadata);
        Assert.Equal(LegacyPayload, serializer.Serialize(message));
    }

    [PostgresFact]
    public async Task ExistingJournalAndReceipt_SurviveSchemaUpgradeAndCannotBeEnrichedByRedelivery()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<AuditingDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, AuditingDbContext.SchemaName).Options;
        await using (var context = new AuditingDbContext(options))
        {
            await JourneyDatabaseOperation.RunAsync(() => context.GetService<IMigrator>().MigrateAsync("20261002171310_OperationObservations"));
        }
        await JourneyDatabaseOperation.RunAsync(() => OperationJournalDatabase.MigrateAsync(database.ConnectionString));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var oldMessage = serializer.Deserialize<OperationObservedV1>(LegacyPayload);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            // 升级前的不可变快照：中央已接纳，来源仍待投递，模拟确认与登记之间重启。
            await using var seed = new NpgsqlCommand("""
                INSERT INTO auditing.inbox ("ConsumerName", "EventName", "MessageId", "ReceivedAt", "AuditPayloadHash")
                VALUES ('auditing.operations', 'auditing.operation-observed.v1', @message, '2026-10-03T00:00:01Z', @hash);
                INSERT INTO auditing.operation_observations
                ("Id", "OperationId", "Source", "Kind", "Phase", "Outcome", "OccurredAt", "ActorId", "TraceId",
                 "HttpMethod", "RouteTemplate", "StatusCode", "DurationMs", "RecordedAt")
                VALUES (@message, @operation, 'legacy-source', 'http', 'finished', 'completed', '2026-10-03T00:00:00Z',
                        NULL, 'legacy-trace', 'PUT', '/api/legacy/{id}', 204, 25, '2026-10-03T00:00:01Z');
                INSERT INTO operation_journal.outbox
                ("Id", "EventName", "Payload", "OccurredAt", "AttemptCount", "OperationId", "Phase", "Source")
                VALUES (@message, 'auditing.operation-observed.v1', @payload, '2026-10-03T00:00:00Z', 0,
                        @operation, 'finished', 'legacy-source');
                """, connection);
            seed.Parameters.AddWithValue("message", oldMessage.EventId);
            seed.Parameters.AddWithValue("operation", oldMessage.OperationId);
            seed.Parameters.AddWithValue("hash", LegacyHash);
            seed.Parameters.AddWithValue("payload", LegacyPayload);
            await seed.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(oldMessage.OccurredAt.AddMinutes(1)));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddSingleton<IIntegrationEventSerializer>(serializer);
        services.AddAuditingPostgresStorage(database.ConnectionString);
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        services.AddScoped<IIntegrationEventProcessor, OperationObservationIngestion>();
        await using var application = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = application.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
        Assert.True((await journal.AppendAsync(oldMessage)).IsSuccess);
        var pending = Assert.Single(await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(10, oldMessage.OccurredAt.AddMinutes(1)));
        Assert.Equal(LegacyPayload, pending.Payload);
        var consumer = scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>();
        Assert.True(await consumer.HandleAsync(pending.ToEnvelope()));
        var enriched = oldMessage with { Metadata = new OperationDetails { Action = "legacy.update", ExecutionRole = "endpoint" } };
        Assert.True((await journal.AppendAsync(enriched)).IsFailure);
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(enriched, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(oldMessage with { DurationMs = 99 }, serializer).ToEnvelope()));
        Assert.True(await consumer.HandleAsync(pending.ToEnvelope()));
        var found = await scope.ServiceProvider.GetRequiredService<IOperationObservationStore>()
            .QueryAsync(new OperationQuery(1, 100, OperationId: oldMessage.OperationId));
        Assert.Equal(1, found.Total);
        var original = Assert.Single(found.Operations);
        Assert.Null(original.Metadata);
        Assert.Equal("completed", original.Outcome);
        Assert.Equal(25, original.DurationMs);
    }
}
