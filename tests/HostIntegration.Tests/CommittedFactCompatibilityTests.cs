using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CommittedFactCompatibilityTests
{
    // 4d17a68 的 AuditFact / SettingCommittedV1 源码独立编译所得，不能由当前代码重新计算期望值。
    private const string LegacyPayload = """
        {"eventName":"platform.setting-committed.v1","key":"audit.legacy","operation":"changed","version":2,"actorId":"42","traceId":"legacy-trace","correlationId":"legacy-correlation","eventId":"11111111-1111-1111-1111-111111111111","occurredAt":"2026-10-03T00:00:00+00:00"}
        """;
    private const string LegacyHash = "FC0E4A853023A9BCDBEB5A81922AB7702FB34EE09248301E43D7A40BD3D601AF";

    [Fact]
    public void SettingWithoutExecution_KeepsFrozenV1Payload()
    {
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var message = serializer.Deserialize<SettingCommittedV1>(LegacyPayload);
        Assert.Null(message.Execution);
        Assert.Equal(LegacyPayload, serializer.Serialize(message));
    }

    [PostgresFact]
    public async Task LegacyFactAndFingerprint_SurviveUpgrade_WithoutInventingAnExecution()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<AuditingDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, AuditingDbContext.SchemaName).Options;
        await using (var context = new AuditingDbContext(options))
        {
            await context.GetService<IMigrator>().MigrateAsync("20261002222303_ScheduleExecutionCorrelation");
        }
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var message = serializer.Deserialize<SettingCommittedV1>(LegacyPayload);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO auditing.inbox ("ConsumerName", "EventName", "MessageId", "ReceivedAt", "AuditPayloadHash")
                VALUES ('auditing.entries', 'platform.setting-committed.v1', @message, '2026-10-03T00:00:01Z', @hash);
                INSERT INTO auditing.audit_entries
                ("Id", "MessageId", "EventName", "Source", "Action", "SubjectType", "SubjectId", "SubjectVersion",
                 "ActorId", "OccurredAt", "TraceId", "CorrelationId", "RecordedAt", "Version")
                VALUES (99, @message, 'platform.setting-committed.v1', 'platform', 'platform.setting.changed',
                        'global-setting', 'audit.legacy', 2, '42', '2026-10-03T00:00:00Z', 'legacy-trace',
                        'legacy-correlation', '2026-10-03T00:00:01Z', 1);
                """, connection);
            seed.Parameters.AddWithValue("message", message.EventId);
            seed.Parameters.AddWithValue("hash", LegacyHash);
            await seed.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(message.OccurredAt.AddMinutes(1)));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddSingleton<IIntegrationEventSerializer>(serializer);
        services.AddAuditingPostgresStorage(database.ConnectionString);
        services.AddScoped<IIntegrationEventProcessor, PlatformAuditIngestion>();
        await using var application = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = application.CreateAsyncScope();
        var consumer = scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>();
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        var invented = message with
        {
            Execution = new ExecutionOrigin(Guid.NewGuid(), "platform", Guid.NewGuid(), "gateway",
            "original-user", message.TraceId, message.CorrelationId)
        };
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(invented, serializer).ToEnvelope()));
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        var page = await scope.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(new AuditQuery(1, 10) { Source = "platform" });
        Assert.Equal(1, page.Total);
        var fact = Assert.Single(page.Entries).Fact;
        Assert.Equal(message.EventId, fact.MessageId);
        Assert.Null(fact.Execution);
        Assert.Equal("42", fact.ActorId);
        Assert.Null(fact.RelatedSubject);
        var ingestion = scope.ServiceProvider.GetRequiredService<AuditIngestion>();
        Assert.True((await ingestion.IngestAsync(fact with { RelatedSubject = new("identity", "user", "42") })).IsFailure);

        var related = fact with { MessageId = Guid.NewGuid(), RelatedSubject = new("identity", "user", "42") };
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(related)).Value);
        Assert.Equal(IngestionOutcome.Duplicate, (await ingestion.IngestAsync(related)).Value);
        Assert.True((await ingestion.IngestAsync(related with { RelatedSubject = new("identity", "user", "43") })).IsFailure);
        var matched = await scope.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(new AuditQuery(1, 10)
        { RelatedContext = "identity", RelatedSubjectType = "user", RelatedSubjectId = "42" });
        Assert.Equal(new AuditSubjectReference("identity", "user", "42"), Assert.Single(matched.Entries).Fact.RelatedSubject);
    }
}
