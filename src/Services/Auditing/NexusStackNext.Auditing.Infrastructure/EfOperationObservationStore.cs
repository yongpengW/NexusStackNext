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
        var operations = context.OperationObservations.AsNoTracking().Where(item => item.Data.Phase == "finished"
            || !context.OperationObservations.Any(other => other.Data.Source == item.Data.Source
                && other.Data.OperationId == item.Data.OperationId && other.Data.Phase == "finished"));
        operations = operations.Where(query.Predicate());
        var total = await operations.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var page = await operations.OrderByDescending(item => item.Data.OccurredAt)
            .ThenBy(item => item.Data.Source).ThenBy(item => item.Data.OperationId)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit)
            .Select(item => new OperationSummary(item.Data.OperationId.Value, item.Data.Source, item.Data.Kind, item.Data.TraceId,
                item.Data.ActorId, item.Data.HttpMethod, item.Data.RouteTemplate,
                item.Data.Phase == "started" ? item.Data.OccurredAt : context.OperationObservations
                    .Where(started => started.Data.Source == item.Data.Source && started.Data.OperationId == item.Data.OperationId
                        && started.Data.Phase == "started").Select(started => (DateTimeOffset?)started.Data.OccurredAt).SingleOrDefault(),
                item.Data.Phase == "finished" ? item.Data.OccurredAt : null,
                item.Data.Outcome ?? "unconfirmed", item.Data.StatusCode, item.Data.DurationMs)
            { Metadata = item.Data.Metadata })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new OperationPage(page, total);
    }
}
