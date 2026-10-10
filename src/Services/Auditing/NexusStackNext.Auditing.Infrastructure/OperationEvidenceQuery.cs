using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure.Persistence;

namespace NexusStackNext.Auditing.Infrastructure;

internal static class OperationEvidenceQuery
{
    internal static IQueryable<OperationSummary> Select(AuditingDbContext context, OperationQuery query) =>
        context.OperationObservations.AsNoTracking().Where(item => item.Data.Phase == "finished"
            || !context.OperationObservations.Any(other => other.Data.Source == item.Data.Source
                && other.Data.OperationId == item.Data.OperationId && other.Data.Phase == "finished"))
            .Where(query.Predicate()).OrderByDescending(item => item.Data.OccurredAt)
            .ThenBy(item => item.Data.Source).ThenBy(item => item.Data.OperationId)
            .Select(item => new OperationSummary(item.Data.OperationId.Value, item.Data.Source, item.Data.Kind, item.Data.TraceId,
                item.Data.ActorId, item.Data.HttpMethod, item.Data.RouteTemplate,
                item.Data.Phase == "started" ? item.Data.OccurredAt : context.OperationObservations
                    .Where(started => started.Data.Source == item.Data.Source && started.Data.OperationId == item.Data.OperationId
                        && started.Data.Phase == "started").Select(started => (DateTimeOffset?)started.Data.OccurredAt).SingleOrDefault(),
                item.Data.Phase == "finished" ? item.Data.OccurredAt : null,
                item.Data.Outcome ?? "unconfirmed", item.Data.StatusCode, item.Data.DurationMs)
            { Metadata = item.Data.Metadata });
}
