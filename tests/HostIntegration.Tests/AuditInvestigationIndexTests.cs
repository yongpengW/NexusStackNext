using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class AuditInvestigationIndexTests
{
    [PostgresFact]
    public async Task SelectiveInvestigations_UseIndexesInActualStoreSql()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var operationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var rootId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        // 这是迁移/存储查询计划检查的分布夹具；业务结果仍通过两个公开查询端口读取。
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO auditing.audit_entries
            ("Id", "MessageId", "EventName", "Source", "Action", "SubjectType", "SubjectId", "SubjectVersion", "ActorId",
             "OccurredAt", "TraceId", "CorrelationId", "RecordedAt", "Version", "OperationId", "OperationSource", "RootOperationId", "RootSource")
            SELECT i, md5('fact' || i::text)::uuid, 'platform.setting-committed.v1', 'platform', 'platform.setting.changed',
                   'global-setting', i::text, 2, 'actor-' || i::text, @now - interval '1 minute', 'trace-' || i::text,
                   'correlation-' || i::text, @now, 1, CASE WHEN i = 17 THEN @operation ELSE md5('operation' || i::text)::uuid END,
                   'platform', CASE WHEN i = 17 THEN @root ELSE md5('root' || i::text)::uuid END, 'gateway'
            FROM generate_series(1, 4000) AS i;
            INSERT INTO auditing.operation_observations
            ("Id", "OperationId", "Source", "Kind", "Phase", "Outcome", "OccurredAt", "ActorId", "TraceId", "DurationMs",
             "RecordedAt", "Action", "ExecutionRole", "SubjectType", "SubjectIdKind", "SubjectId", "TaskId", "TaskEpoch", "RootOperationId", "RootSource")
            SELECT md5('observation' || i::text)::uuid, CASE WHEN i = 17 THEN @operation ELSE md5('operation' || i::text)::uuid END,
                   'pricing', 'task', 'finished', 'completed', @now - interval '1 minute', NULL, 'trace-' || i::text, 5,
                   @now, 'pricing.calculate', 'task', 'PriceQuote', 'int64', i::text,
                   CASE WHEN i = 17 THEN @operation ELSE md5('task' || i::text)::uuid END, 2,
                   CASE WHEN i = 17 THEN @root ELSE md5('root' || i::text)::uuid END, 'gateway'
            FROM generate_series(1, 4000) AS i;
            UPDATE auditing.audit_entries SET "RelatedContext" = 'identity', "RelatedSubjectType" = 'user', "RelatedSubjectId" = "Id"::text;
            ANALYZE auditing.audit_entries;
            ANALYZE auditing.operation_observations;
            """, connection))
        {
            seed.Parameters.AddWithValue("now", now);
            seed.Parameters.AddWithValue("operation", operationId);
            seed.Parameters.AddWithValue("root", rootId);
            await seed.ExecuteNonQueryAsync();
        }
        var capture = new QueryCapture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddAuditingPostgresStorage(database.ConnectionString);
        services.ConfigureDbContext<AuditingDbContext>(options => options.AddInterceptors(capture));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var facts = scope.ServiceProvider.GetRequiredService<IAuditEntryStore>();
        foreach (var (query, index) in new[]
        {
            (new AuditQuery(1, 10) { SubjectType = "global-setting", SubjectId = "17" }, "ix_audit_entries_subject"),
            (new AuditQuery(1, 10) { ActorId = "actor-17" }, "ix_audit_entries_actor"),
            (new AuditQuery(1, 10) { OperationId = operationId }, "ix_audit_entries_operation"),
            (new AuditQuery(1, 10) { RootOperationId = rootId }, "ix_audit_entries_root"),
            (new AuditQuery(1, 10) { RelatedContext = "identity", RelatedSubjectType = "user", RelatedSubjectId = "17" }, "ix_audit_entries_related"),
        })
        {
            capture.Commands.Clear();
            Assert.Equal(17, Assert.Single((await facts.QueryAsync(query)).Entries).Id.Value);
            await AssertIndexAsync(connection, capture.Commands, index);
        }
        var operations = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        foreach (var (query, index) in new[]
        {
            (new OperationQuery(1, 10) { SubjectType = "PriceQuote", SubjectId = "17" }, "ix_operation_observations_subject"),
            (new OperationQuery(1, 10) { TaskId = operationId, TaskEpoch = 2 }, "ix_operation_observations_task"),
            (new OperationQuery(1, 10) { RootOperationId = rootId }, "ix_operation_observations_root"),
        })
        {
            capture.Commands.Clear();
            Assert.Equal(operationId, Assert.Single((await operations.QueryAsync(query)).Operations).OperationId);
            await AssertIndexAsync(connection, capture.Commands, index);
        }
        capture.Commands.Clear();
        Assert.Empty((await facts.QueryAsync(new AuditQuery(1, 10) { From = now.AddDays(-2), To = now.AddDays(-1) })).Entries);
        await AssertIndexAsync(connection, capture.Commands, "ix_audit_entries_occurred");
    }

    private static async Task AssertIndexAsync(NpgsqlConnection connection, List<CapturedQuery> queries, string expectedIndex)
    {
        Assert.NotEmpty(queries);
        foreach (var query in queries)
        {
            await using var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + query.Sql, connection);
            foreach (var parameter in query.Parameters) { explain.Parameters.Add(parameter); }
            using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
            Assert.Contains(expectedIndex, Indexes(plan.RootElement));
        }
    }

    private static IEnumerable<string> Indexes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "Index Name") { yield return property.Value.GetString()!; }
                foreach (var index in Indexes(property.Value)) { yield return index; }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var index in Indexes(item)) { yield return index; }
            }
        }
    }

    private sealed record CapturedQuery(string Sql, NpgsqlParameter[] Parameters);

    private sealed class QueryCapture : DbCommandInterceptor
    {
        public List<CapturedQuery> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(new CapturedQuery(command.CommandText,
                command.Parameters.Cast<NpgsqlParameter>().Select(parameter => (NpgsqlParameter)((ICloneable)parameter).Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}
