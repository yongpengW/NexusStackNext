using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationObservationPersistenceTests
{
    [PostgresFact]
    public async Task FailedObservation_RollsBackInboxAndFingerprint_ThenRedeliveryPreservesCommittedContent()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        var migration = await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing");
        Assert.Equal(0, migration.ExitCode);
        Assert.Contains("Auditing migrations applied.", migration.Output, StringComparison.Ordinal);

        var occurredAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var message = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "operation-atomic-source",
            Kind = "http",
            Phase = "finished",
            Outcome = "completed",
            OccurredAt = occurredAt,
            ActorId = "trusted-actor",
            TraceId = "operation-atomic-trace",
            HttpMethod = "PUT",
            RouteTemplate = "/api/platform/settings/{key}",
            StatusCode = 204,
            DurationMs = 25,
            Metadata = new OperationDetails
            {
                Action = "platform.setting.update",
                ExecutionRole = "endpoint",
                Description = "修改全局设置",
                SubjectType = "Setting",
                SubjectIdKind = "int64",
                SubjectId = "9007199254740993",
                SpanId = "1234567890abcdef",
                ParentSpanId = "abcdef1234567890",
                CorrelationId = "settings-change-62",
            },
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(occurredAt.AddMinutes(1)));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        services.AddAuditingPostgresStorage(database.ConnectionString);
        services.AddKeyedScoped<IIntegrationEventProcessor, OperationObservationIngestion>(OperationObservedV1.Name);
        await using var application = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await using var fault = new NpgsqlConnection(database.ConnectionString);
        await fault.OpenAsync();
        await using (var reject = new NpgsqlCommand(
            $"ALTER TABLE auditing.operation_observations ADD CONSTRAINT reject_test_operation CHECK (\"Id\" <> '{message.EventId:D}')", fault))
        {
            await reject.ExecuteNonQueryAsync();
        }
        try
        {
            await using (var scope = application.CreateAsyncScope())
            {
                var failure = await Assert.ThrowsAsync<DbUpdateException>(() => DeliverAsync(scope.ServiceProvider, message));
                var databaseFailure = Assert.IsType<PostgresException>(failure.InnerException);
                Assert.Equal(PostgresErrorCodes.CheckViolation, databaseFailure.SqlState);
                Assert.Equal("reject_test_operation", databaseFailure.ConstraintName);
            }
            await using var reader = application.CreateAsyncScope();
            var missing = await reader.ServiceProvider.GetRequiredService<IOperationObservationStore>()
                .QueryAsync(new OperationQuery(1, 100, message.Source, message.OperationId));
            Assert.Equal(0, missing.Total);
            Assert.Empty(missing.Operations);
        }
        finally
        {
            await using var release = new NpgsqlCommand("ALTER TABLE auditing.operation_observations DROP CONSTRAINT reject_test_operation", fault);
            await release.ExecuteNonQueryAsync();
        }

        await using (var recovered = application.CreateAsyncScope())
        {
            Assert.True(await DeliverAsync(recovered.ServiceProvider, message));
        }
        await using (var replay = application.CreateAsyncScope())
        {
            Assert.True(await DeliverAsync(replay.ServiceProvider, message));
            Assert.False(await DeliverAsync(replay.ServiceProvider, message with { Outcome = "accepted", StatusCode = 202, ActorId = "cannot-overwrite" }));
            foreach (var changed in new OperationDetails?[]
            {
                null,
                message.Metadata with { Action = "different.action" },
                message.Metadata with { Description = "不能覆盖" },
                message.Metadata with { ExecutionRole = "proxy" },
                message.Metadata with { SubjectType = "AnotherSubject" },
                message.Metadata with { SubjectId = "9007199254740994" },
                message.Metadata with { SubjectIdKind = "guid", SubjectId = "98e26a25-036d-49cb-aaef-2e6197a33ce0" },
                message.Metadata with { SpanId = "abcdef1234567890" },
                message.Metadata with { ParentSpanId = "1234567890abcdef" },
                message.Metadata with { CorrelationId = "different-correlation" },
            })
            {
                Assert.False(await DeliverAsync(replay.ServiceProvider, message with { Metadata = changed }));
            }
        }
        await using var verification = application.CreateAsyncScope();
        var page = await verification.ServiceProvider.GetRequiredService<IOperationObservationStore>()
            .QueryAsync(new OperationQuery(1, 100, message.Source, message.OperationId));
        Assert.Equal(1, page.Total);
        var saved = Assert.Single(page.Operations);
        Assert.Equal(message.OperationId, saved.OperationId);
        Assert.Equal("completed", saved.Outcome);
        Assert.Equal(204, saved.StatusCode);
        Assert.Equal("trusted-actor", saved.ActorId);
        Assert.Equal(occurredAt, saved.FinishedAt);
        Assert.Null(saved.StartedAt);
        Assert.NotNull(saved.Metadata);
        Assert.Equal("platform.setting.update", saved.Metadata.Action);
        Assert.Equal("修改全局设置", saved.Metadata.Description);
        Assert.Equal("endpoint", saved.Metadata.ExecutionRole);
        Assert.Equal("Setting", saved.Metadata.SubjectType);
        Assert.Equal("int64", saved.Metadata.SubjectIdKind);
        Assert.Equal("9007199254740993", saved.Metadata.SubjectId);
        Assert.Equal("1234567890abcdef", saved.Metadata.SpanId);
        Assert.Equal("abcdef1234567890", saved.Metadata.ParentSpanId);
        Assert.Equal("settings-change-62", saved.Metadata.CorrelationId);
    }

    private static Task<bool> DeliverAsync(IServiceProvider services, OperationObservedV1 observation) =>
        services.GetRequiredKeyedService<IIntegrationEventProcessor>(OperationObservedV1.Name).HandleAsync(new EventEnvelope
        {
            MessageId = observation.EventId,
            EventName = observation.EventName,
            OccurredAt = observation.OccurredAt,
            Payload = services.GetRequiredService<IIntegrationEventSerializer>().Serialize(observation),
        });
}
