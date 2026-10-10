using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Exports;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Contracts;
using Npgsql;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class AuditExportArtifactQuery(AuditingDbContext database, ICurrentUser user, IExportFiles files) : IQueryHandler<GetAuditExportArtifact, AuditExportArtifact>
{
    public async Task<Result<AuditExportArtifact>> HandleAsync(GetAuditExportArtifact query, CancellationToken cancellationToken = default)
    {
        if (query.ExportId == Guid.Empty) { return Failure("invalid"); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var id = new AuditExportId(query.ExportId);
            var export = await database.Exports.AsNoTracking().Where(x => x.Id == id && x.OwnerId == user.UserId).Select(x => new { x.FileId }).SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
            if (export is null) { return Failure("not_found"); }
            if (export.FileId is null) { return Failure("not_ready"); }
            var current = await files.AvailabilityAsync(query.ExportId, export.FileId.Value, budget.Token).ConfigureAwait(false);
            if (current.IsFailure) { return Result.Failure<AuditExportArtifact>(current.Error); }
            return Result.Success(new AuditExportArtifact(export.FileId.Value, current.Value.State,
                current.Value.State == "Available" ? "/api/files/" + export.FileId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("unavailable"); }
        catch (NpgsqlException) { return Failure("unavailable"); }
    }

    private static Result<AuditExportArtifact> Failure(string code) => Result.Failure<AuditExportArtifact>(new Error("auditing.export." + code, "原导出不存在、尚未生成或暂不可用。"));
}
