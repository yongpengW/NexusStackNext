using System.Globalization;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExportArtifactQuery(PricingDbContext database, ICurrentUser user, IExportFiles files) : IQueryHandler<GetPricingExportArtifact, PricingExportArtifact>
{
    public async Task<Result<PricingExportArtifact>> HandleAsync(GetPricingExportArtifact query, CancellationToken cancellationToken = default)
    {
        var owner = user.UserId;
        if (string.IsNullOrWhiteSpace(owner)) { return Missing(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            readBudget.CancelAfter(TimeSpan.FromSeconds(5));
            var export = await PricingExportReadModel.ReadAsync(database, query.ExportId, owner, readBudget.Token).ConfigureAwait(false);
            if (export?.FileId is not { } fileId || export.UploadId is not { } uploadId || export.PublicationId is not { } publicationId) { return Missing(); }
            var available = await files.AvailabilityAsync(uploadId, fileId, budget.Token).ConfigureAwait(false);
            if (available.IsFailure) { return Result.Failure<PricingExportArtifact>(available.Error); }
            var status = export.Status();
            return Result.Success(new PricingExportArtifact(fileId, publicationId, available.Value.State,
                available.Value.State == "Available" ? "/api/files/" + fileId.ToString(CultureInfo.InvariantCulture) : null, status.PublishedAt, status.ExpiresAt));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (NpgsqlException) { return Unavailable(); }
    }

    private static Result<PricingExportArtifact> Missing() => Result.Failure<PricingExportArtifact>(new Error("pricing.export.not_found", "自己的成果尚不可用。"));
    private static Result<PricingExportArtifact> Unavailable() => Result.Failure<PricingExportArtifact>(new Error("pricing.export.unavailable", "成果状态暂不可用，请稍后查看。"));
}
