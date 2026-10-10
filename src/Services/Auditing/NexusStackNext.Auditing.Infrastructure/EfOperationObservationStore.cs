using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class EfOperationObservationStore(AuditingDbContext context, IClock clock, PostgresAuditCapacity capacity) : IOperationObservationStore
{
    public Task<int> DeleteExpiredAsync(DateTimeOffset recordedBefore, int maxOperations, CancellationToken cancellationToken = default)
    {
        if (recordedBefore == default || recordedBefore.Offset != TimeSpan.Zero) { throw new ArgumentException("截止时刻必须为 UTC。", nameof(recordedBefore)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOperations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxOperations, 1000);
        return capacity.RunAsync(budgetToken => context.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            await capacity.ConfigureTransactionAsync(token).ConfigureAwait(false);
            // 先限制候选，再尝试已有接纳锁；正在接收新阶段的操作留到下一轮。
            var operations = await context.Database.SqlQuery<ExpiredOperation>($"""
                WITH candidates AS MATERIALIZED (
                    SELECT o."Source", o."OperationId"
                    FROM auditing.operation_observations o
                    WHERE o."RecordedAt" < {recordedBefore}
                        AND NOT EXISTS (SELECT 1 FROM auditing.operation_observations newer
                            WHERE newer."Source" = o."Source" AND newer."OperationId" = o."OperationId"
                                AND newer."RecordedAt" >= {recordedBefore})
                    GROUP BY o."Source", o."OperationId"
                    ORDER BY max(o."RecordedAt"), o."Source", o."OperationId"
                    LIMIT {maxOperations}
                )
                SELECT "Source", "OperationId" FROM candidates
                WHERE pg_try_advisory_xact_lock(hashtextextended('auditing.operation/' || "Source" || '/' || "OperationId"::text, 0))
                """).ToArrayAsync(token).ConfigureAwait(false);
            var keys = JsonSerializer.Serialize(operations);
            // Read Committed 的下一条语句重新确认期限，看到接纳锁取得前刚提交的新阶段。
            var counts = await context.Database.SqlQuery<int>($"""
                WITH removed AS (
                    DELETE FROM auditing.operation_observations o
                    USING jsonb_to_recordset({keys}::jsonb) AS selected("Source" text, "OperationId" uuid)
                    WHERE o."Source" = selected."Source" AND o."OperationId" = selected."OperationId"
                        AND NOT EXISTS (SELECT 1 FROM auditing.operation_observations newer
                            WHERE newer."Source" = o."Source" AND newer."OperationId" = o."OperationId"
                                AND newer."RecordedAt" >= {recordedBefore})
                    RETURNING o."Id"
                ), receipts AS (
                    DELETE FROM auditing.inbox i USING removed
                    WHERE i."ConsumerName" = {OperationObservationIngestion.ConsumerName}
                        AND i."EventName" = {OperationObservedV1.Name} AND i."MessageId" = removed."Id"
                    RETURNING i."MessageId"
                )
                SELECT count(*)::integer AS "Value" FROM removed
                """).ToArrayAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return counts.Single();
        }, budgetToken), cancellationToken);
    }

    private sealed record ExpiredOperation(string Source, Guid OperationId);

    public Task<Result<IngestionOutcome>> AcceptAsync(OperationObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        // 指纹的原始字段形状保持稳定，不能因领域 ID 包装类型改变而拒绝同一消息重投。
        var data = observation.Data;
        var legacyFields = new
        {
            OperationId = data.OperationId.Value,
            data.Source,
            data.Kind,
            data.Phase,
            data.Outcome,
            data.OccurredAt,
            data.ActorId,
            data.TraceId,
            data.HttpMethod,
            data.RouteTemplate,
            data.StatusCode,
            data.DurationMs,
        };
        // 旧记录保持原指纹。扩展字段显式参与指纹，不能重投补写已经接纳的阶段。
        // 契约中的可选字段忽略 null；将来增加字段时也不能改变已有消息的身份。
        var metadata = data.Metadata;
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes<object>(metadata is null ? legacyFields : new
        {
            Observation = legacyFields,
            Metadata = new OperationDetails
            {
                Action = metadata.Action,
                ExecutionRole = metadata.ExecutionRole,
                Description = metadata.Description,
                SubjectType = metadata.SubjectType,
                SubjectIdKind = metadata.SubjectIdKind,
                SubjectId = metadata.SubjectId,
                SpanId = metadata.SpanId,
                ParentSpanId = metadata.ParentSpanId,
                CorrelationId = metadata.CorrelationId,
                RootOperationId = metadata.RootOperationId,
                RootSource = metadata.RootSource,
                ParentOperationId = metadata.ParentOperationId,
                ParentSource = metadata.ParentSource,
                InitiatorId = metadata.InitiatorId,
                TaskId = metadata.TaskId,
                TaskEpoch = metadata.TaskEpoch,
                SchedulePlanId = metadata.SchedulePlanId,
                ScheduleExpectedVersion = metadata.ScheduleExpectedVersion,
                ScheduleDecisionId = metadata.ScheduleDecisionId,
            },
        })));
        return capacity.RunAsync(budgetToken => context.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            await capacity.ConfigureTransactionAsync(token).ConfigureAwait(false);
            // 不同消息也不能同时登记同一阶段；锁只覆盖 Auditing 自己的数据与事务。
            var operationKey = "auditing.operation/" + observation.Data.Source + "/" + observation.Data.OperationId.Value;
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({operationKey}, 0))", token).ConfigureAwait(false);
            var first = await new EfInboxStore<AuditingDbContext>(context).TryBeginProcessingAsync(
                OperationObservationIngestion.ConsumerName, OperationObservedV1.Name, observation.Id.Value,
                observation.RecordedAt, token).ConfigureAwait(false);
            var receipt = await context.Inbox.SingleAsync(item => item.ConsumerName == OperationObservationIngestion.ConsumerName
                && item.EventName == OperationObservedV1.Name && item.MessageId == observation.Id.Value, token).ConfigureAwait(false);
            var fingerprint = context.Entry(receipt).Property<string?>(AuditingDbContext.PayloadHashProperty);
            if (!first)
            {
                return fingerprint.CurrentValue == hash ? Result.Success(IngestionOutcome.Duplicate)
                    : Result.Failure<IngestionOutcome>(OperationObservationIngestion.MessageConflict);
            }
            if (await context.OperationObservations.AnyAsync(item => item.Data.Source == observation.Data.Source
                && item.Data.OperationId == observation.Data.OperationId && item.Data.Phase == observation.Data.Phase, token).ConfigureAwait(false))
            {
                return Result.Failure<IngestionOutcome>(OperationObservationIngestion.PhaseConflict);
            }
            fingerprint.CurrentValue = hash;
            context.OperationObservations.Add(observation);
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Result.Success(IngestionOutcome.Accepted);
        }, budgetToken), cancellationToken);
    }

    public async Task<OperationPage> QueryAsync(OperationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.Normalize(clock.UtcNow);
        if (query.Validate().IsFailure) { throw new ArgumentException("操作查询条件无效。", nameof(query)); }
        // 每个操作选择完成记录；没有完成时才选择开始记录，不依赖投递顺序。
        var operations = OperationEvidenceQuery.Select(context, query);
        var total = await operations.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var page = await operations.Skip((query.Page - 1) * query.Limit).Take(query.Limit)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new OperationPage(page, total);
    }
}
