using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>宿主显式启用导出生成及持久发布处理器。</summary>
public static class PricingExportProcessingServices
{
    /// <summary>装配处理器；Files 端口由宿主明确提供。</summary>
    /// <param name="services">容器。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddPricingExportProcessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<PricingExportGenerationBudget>();
        services.AddScoped<PricingExportExecution>();
        services.AddScoped<PricingExportPublicationExecution>();
        services.AddScoped<ICommandHandler<GeneratePricingExport, PricingExportStatus>, PricingExportProducer>();
        services.AddScoped<ICommandHandler<PublishPricingExport, PricingExportStatus>, PricingExportProducer>();
        services.AddScoped<IQueryHandler<GetPricingExportArtifact, PricingExportArtifact>, PricingExportArtifactQuery>();
        return services;
    }
}

internal sealed class PricingExportProducer(PricingDbContext database, PricingExportOptions options, IExportFiles files,
    PricingExportGenerationBudget generationBudget, IBackgroundExecutionObservation observations,
    PricingExportExecution generation, PricingExportPublicationExecution publication) : ICommandHandler<GeneratePricingExport, PricingExportStatus>, ICommandHandler<PublishPricingExport, PricingExportStatus>
{
    public async Task<Result<PricingExportStatus>> HandleAsync(GeneratePricingExport command, CancellationToken cancellationToken = default)
    {
        if (!generationBudget.TryEnter()) { return Failure("pricing.export.generator_busy"); }
        try
        {
            return await observations.ObserveAsync(new TaskExecutionDescriptor("pricing.export.generate", command.Lease.ExportId, command.Lease.Epoch),
                () => PrepareAsync(command.Lease.ExportId, cancellationToken), async input =>
                {
                    var export = input.Export;
                    if (export is null || export.State != "Generating" || export.Epoch != command.Lease.Epoch || export.UploadId != command.Lease.UploadId
                        || export.LeaseUntil is null || export.LeaseUntil <= input.Now || export.MaxLeaseUntil is null || export.MaxLeaseUntil <= input.Now)
                    { return Failure("pricing.export.lease_lost"); }
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var remaining = export.MaxLeaseUntil.Value - input.Now - input.Elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) { return Failure("pricing.export.lease_lost"); }
                    budget.CancelAfter(remaining);
                    // 快照摘要继续使用版本化精确 CSV；输出 ZIP 使用另外的实际文件摘要。
                    using var snapshot = new MemoryStream();
                    await PricingCsvV1.WriteAsync(export.Rows, snapshot, cancellationToken: budget.Token).ConfigureAwait(false);
                    var snapshotDigest = Convert.ToHexStringLower(SHA256.HashData(snapshot.GetBuffer().AsSpan(0, checked((int)snapshot.Length))));
                    if (snapshotDigest != export.SnapshotDigest || snapshot.Length != export.SnapshotLength) { return Failure("pricing.export.snapshot_corrupt"); }
                    var original = await files.FindAsync(new ExportFileLookup(command.Lease.UploadId, export.OwnerId, export.Id.Value, export.Format), budget.Token).ConfigureAwait(false);
                    if (original.IsFailure) { return Result.Failure<PricingExportStatus>(original.Error); }
                    if (original.Value is { Stage: "Staged" } sealedCandidate)
                    {
                        // 已封存候选不再次渲染；原 ZIP 字节、摘要与 FileId 继续属于同一身份。
                        return await generation.HandleAsync(new SelectPricingExportPublication(export.Id.Value, command.Lease.Epoch, Guid.NewGuid(), sealedCandidate), budget.Token).ConfigureAwait(false);
                    }
                    var directory = string.IsNullOrWhiteSpace(options.TemporaryDirectory)
                        ? Path.Combine(Path.GetTempPath(), "nsn-pricing-exports") : options.TemporaryDirectory;
                    Directory.CreateDirectory(directory);
                    var streamOptions = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
                        BufferSize = 65_536,
                    };
                    if (!OperatingSystem.IsWindows()) { streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                    // Unix DeleteOnClose 在托管 Dispose 中删除；SIGKILL 不执行它。写入前先 unlink，
                    // 字节只由已打开的句柄持有，内核在进程死亡时释放。Windows 使用原生 delete-on-close。
                    var temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + "." + export.Format + ".tmp");
                    await using var content = new FileStream(temporaryPath, streamOptions);
                    if (!OperatingSystem.IsWindows()) { File.Delete(temporaryPath); }
                    if (export.Format == "xlsx") { await PricingXlsxV1.WriteAsync(export.Rows, content, options.MaxOutputBytes, budget.Token).ConfigureAwait(false); }
                    else { await PricingCsvV1.WriteAsync(export.Rows, content, options.MaxOutputBytes, budget.Token).ConfigureAwait(false); }
                    content.Position = 0;
                    var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, budget.Token).ConfigureAwait(false));
                    var description = new GeneratedFileDescriptionV1(export.OwnerId, export.Id.Value, digest, content.Length, export.Format, 1, 1);
                    if (export.Format == "csv" && (digest != export.SnapshotDigest || content.Length != export.SnapshotLength)) { return Failure("pricing.export.snapshot_corrupt"); }
                    if (original.Value is { } pending && pending.Description != description) { return Failure("pricing.export.candidate_bytes_conflict"); }
                    var uploaded = await files.StageAsync(new ExportFileUpload(command.Lease.UploadId, description), content, budget.Token).ConfigureAwait(false);
                    if (uploaded.IsFailure) { return Result.Failure<PricingExportStatus>(uploaded.Error); }
                    return await generation.HandleAsync(new SelectPricingExportPublication(export.Id.Value, command.Lease.Epoch, Guid.NewGuid(), uploaded.Value), budget.Token).ConfigureAwait(false);
                }, ClassifyGeneration, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException error) { return Failure(error.Message == "pricing.export.invalid_workbook" ? "pricing.export.invalid_workbook" : "pricing.export.output_limit"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Failure("pricing.export.storage_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("pricing.export.execution_timeout"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Failure("pricing.export.unavailable"); }
        finally { generationBudget.Exit(); }
    }

    public async Task<Result<PricingExportStatus>> HandleAsync(PublishPricingExport command, CancellationToken cancellationToken = default)
    {
        try
        {
            return await observations.ObserveAsync(new RecoveryExecutionDescriptor("pricing.export.publish", "PricingExportPublication", "guid",
                command.Lease.PublicationId.ToString("D", CultureInfo.InvariantCulture)),
                () => PrepareAsync(command.Lease.ExportId, cancellationToken), async input =>
                {
                    var export = input.Export;
                    if (export is null || export.PublicationId != command.Lease.PublicationId || export.FileId is null || export.UploadId is null
                        || export.ArtifactDigest is null || export.ArtifactLength is null)
                    { return Failure("pricing.export.lease_lost"); }
                    if (export.State == "Succeeded")
                    { return Result.Success((await PricingExportReadModel.ReadAsync(database, export.Id.Value, null, cancellationToken).ConfigureAwait(false))!.Status()); }
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    budget.CancelAfter(options.MaxExecutionDuration);
                    var intent = await database.ExportPublications.AsNoTracking().SingleOrDefaultAsync(x => x.ExportId == export.Id, budget.Token).ConfigureAwait(false);
                    if (intent is null || !PricingExportPublicationExecution.Owns(intent, command.Lease.Epoch, input.Now)) { return Failure("pricing.export.lease_lost"); }
                    var remaining = intent.MaxLeaseUntil!.Value - input.Now - input.Elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) { return Failure("pricing.export.lease_lost"); }
                    budget.CancelAfter(remaining);
                    var receipt = await files.PublishAsync(new ExportFilePublication(intent.UploadId, intent.FileId, intent.PublicationId, Description(export)), budget.Token).ConfigureAwait(false);
                    return receipt.IsFailure ? Result.Failure<PricingExportStatus>(receipt.Error)
                        : await publication.HandleAsync(new CompletePricingExportPublication(command.Lease, receipt.Value), budget.Token).ConfigureAwait(false);
                }, ClassifyPublication, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("pricing.export.execution_timeout"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Failure("pricing.export.unavailable"); }
    }

    private async Task<BackgroundExecutionInput<ExportAttempt>> PrepareAsync(Guid exportId, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var id = new PricingExportId(exportId);
        var input = await database.Exports.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { Export = x, Origin = EF.Property<ExecutionOrigin?>(x, PricingExportCommands.OriginProperty) })
            .SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();
        var now = await database.DatabaseTimeAsync(budget.Token).ConfigureAwait(false);
        return new(new(input?.Export, now, elapsed), input?.Origin);
    }

    private static GeneratedFileDescriptionV1 Description(PricingExport export) => new(export.OwnerId, export.Id.Value,
        export.ArtifactDigest!, export.ArtifactLength!.Value, export.Format, 1, 1);
    // 生成阶段完成只证明已选定封存候选；导出成功仍由独立发布阶段裁决。
    private static BackgroundExecutionOutcome ClassifyGeneration(Result<PricingExportStatus> result) => result.IsSuccess
        ? BackgroundExecutionOutcome.Completed
        : result.Error.Code == "pricing.export.lease_lost" ? BackgroundExecutionOutcome.LeaseLost : BackgroundExecutionOutcome.Failed;
    private static BackgroundExecutionOutcome ClassifyPublication(Result<PricingExportStatus> result) => result.IsSuccess && result.Value.State == "Succeeded"
        ? BackgroundExecutionOutcome.Completed : BackgroundExecutionOutcome.Deferred;
    private static Result<PricingExportStatus> Failure(string code) => Result.Failure<PricingExportStatus>(new Error(code, "导出暂未完成，请查看原工作状态。"));
    private sealed record ExportAttempt(PricingExport? Export, DateTimeOffset Now, Stopwatch Elapsed);
}

internal sealed class PricingExportGenerationBudget : IDisposable
{
    private readonly SemaphoreSlim _slots = new(1, 1);
    public bool TryEnter() => _slots.Wait(0);
    public void Exit() => _slots.Release();
    public void Dispose() => _slots.Dispose();
}
