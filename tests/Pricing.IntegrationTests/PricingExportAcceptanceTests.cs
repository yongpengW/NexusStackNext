using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.Files.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingExportAcceptanceTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Changing_only_export_format_conflicts_with_the_original_request_but_new_identity_keeps_the_same_snapshot()
    {
        await using var app = CreateApplication("export-owner");
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var request = new AcceptPricingExport(Guid.NewGuid(), [item]);
        var csv = (await sender.SendAsync(request)).Value;
        var conflict = await sender.SendAsync(request with { Format = "xlsx" });
        Assert.True(conflict.IsFailure);
        Assert.Equal("pricing.export.request_conflict", conflict.Error.Code);
        Assert.Equal(csv, (await sender.SendAsync(request with { Format = "csv" })).Value);
        var xlsxRequest = request with { RequestId = Guid.NewGuid(), Format = "xlsx" };
        var xlsx = (await sender.SendAsync(xlsxRequest)).Value;
        Assert.NotEqual(csv.RequestDigest, xlsx.RequestDigest);
        Assert.Equal(csv.SnapshotDigest, xlsx.SnapshotDigest);
        Assert.Equal(xlsx, (await sender.SendAsync(xlsxRequest)).Value);
    }

    [PostgresFact]
    public async Task Inclusive_date_filters_keep_exact_boundaries_instead_of_truncating_sub_microsecond_input()
    {
        await using var app = CreateApplication("export-owner");
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        Assert.Single((await sender.QueryAsync(new ListPricingExports(AcceptedFrom: accepted.AcceptedAt, AcceptedThrough: accepted.AcceptedAt))).Value.Items);
        Assert.Empty((await sender.QueryAsync(new ListPricingExports(AcceptedFrom: accepted.AcceptedAt.AddTicks(1)))).Value.Items);
        Assert.Empty((await sender.QueryAsync(new ListPricingExports(AcceptedThrough: accepted.AcceptedAt.AddTicks(-1)))).Value.Items);
    }

    [PostgresFact]
    public async Task Empty_or_over_limit_selection_creates_no_work_but_exactly_five_thousand_rows_are_accepted()
    {
        await using var app = CreateApplication("export-owner");
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("pricing.export.empty", (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), []))).Error.Code);
        // 大集合准备只写本例拥有的库；判断仍通过公开命令/查询，不通过表断言。
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var seed = new NpgsqlCommand("""
            INSERT INTO pricing.quotes ("Id", "Cost", "FeeRate", "InputRevision", "CalculatedRevision", "CostingRevision", "Version", "CreatedAt")
            SELECT gen_random_uuid(), 80, 0.2, 1, 0, 0, 1, statement_timestamp() FROM generate_series(1, 5001)
            RETURNING "Id"
            """, connection);
        var ids = new List<Guid>();
        await using (var reader = await seed.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) { ids.Add(reader.GetGuid(0)); }
        }
        Assert.Equal(5001, ids.Count);
        Assert.Equal("pricing.export.too_many_rows", (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), []))).Error.Code);
        Assert.Equal("pricing.export.invalid", (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), ids))).Error.Code);
        Assert.Empty((await sender.QueryAsync(new ListPricingExports())).Value.Items);
        var accepted = await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), ids.Take(5000).ToArray()));
        Assert.True(accepted.IsSuccess, accepted.Error?.Code);
        Assert.Equal(5000, accepted.Value.RowCount);
        Assert.Equal(accepted.Value.ExportId, Assert.Single((await sender.QueryAsync(new ListPricingExports())).Value.Items).ExportId);
    }

    [PostgresFact]
    public async Task Repeated_export_after_quote_change_and_restart_returns_the_original_frozen_snapshot()
    {
        var itemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var request = new AcceptPricingExport(Guid.NewGuid(), [itemId]);
        PricingExportStatus original;
        await using (var app = CreateApplication("export-owner"))
        {
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), itemId, 0, 80m, 0.2m))).IsSuccess);
            var accepted = await sender.SendAsync(request);
            Assert.True(accepted.IsSuccess, accepted.Error?.Code);
            original = accepted.Value;
            Assert.Equal("Queued", original.State);
            Assert.Equal(1, original.RowCount);
            Assert.Equal(64, original.SnapshotDigest.Length);
            Assert.NotEqual(original.SnapshotDigest, original.RequestDigest);
            Assert.NotEqual(default, original.FrozenAt);
            Assert.True(original.AcceptedAt >= original.FrozenAt);
            Assert.Equal("export-owner", original.Audit!.CreatedBy);
            Assert.Null(original.Audit.UpdatedAt);
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), itemId, 1, 120m, 0.2m))).IsSuccess);
        }

        await using var reopened = CreateApplication("export-owner");
        await using var read = reopened.CreateAsyncScope();
        var reopenedSender = read.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal(original, (await reopenedSender.SendAsync(request)).Value);
        Assert.Equal(original, (await reopenedSender.QueryAsync(new GetPricingExport(original.ExportId))).Value);
        var fresh = (await reopenedSender.SendAsync(request with { RequestId = Guid.NewGuid() })).Value;
        Assert.NotEqual(original.ExportId, fresh.ExportId);
        Assert.NotEqual(original.SnapshotDigest, fresh.SnapshotDigest);
    }

    private ServiceProvider CreateApplication(string? owner, bool isRoot = false, PricingExportOptions? exportOptions = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(owner, isRoot));
        services.AddPricingPostgres(database.ConnectionString).AddPricingExportPersistence(exportOptions);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [PostgresFact]
    public async Task Owner_cancels_queued_export_once_and_replay_keeps_original_snapshot_and_audit()
    {
        await using var app = CreateApplication("export-owner");
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var request = new AcceptPricingExport(Guid.NewGuid(), [item]);
        var accepted = (await sender.SendAsync(request)).Value;
        Assert.Equal("pricing.export.cancel_conflict", (await sender.SendAsync(new CancelPricingExport(accepted.ExportId, 0))).Error.Code);
        Assert.Equal(accepted, (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value);
        var canceled = await sender.SendAsync(new CancelPricingExport(accepted.ExportId, accepted.Version));
        Assert.True(canceled.IsSuccess, canceled.Error?.Code);
        Assert.Equal("Canceled", canceled.Value.State);
        Assert.Equal(accepted.Version + 1, canceled.Value.Version);
        Assert.Equal(accepted.SnapshotDigest, canceled.Value.SnapshotDigest);
        Assert.Equal(accepted.AcceptedAt, canceled.Value.AcceptedAt);
        Assert.Equal("export-owner", canceled.Value.Audit!.CreatedBy);
        Assert.Equal("export-owner", canceled.Value.Audit.UpdatedBy);
        Assert.Equal(canceled.Value, (await sender.SendAsync(new CancelPricingExport(accepted.ExportId, accepted.Version))).Value);
        Assert.Equal(canceled.Value, (await sender.SendAsync(request)).Value);
    }

    [PostgresFact]
    public async Task Concurrent_normalized_request_retries_share_one_snapshot_but_another_owner_has_a_separate_identity()
    {
        await using var owner = CreateApplication("export-owner");
        var firstItem = Guid.NewGuid();
        var secondItem = Guid.NewGuid();
        await using (var scope = owner.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), firstItem, 0, 80m, 0.2m))).IsSuccess);
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), secondItem, 0, 120m, 0.2m))).IsSuccess);
        }
        var request = new AcceptPricingExport(Guid.NewGuid(), [firstItem, secondItem]);
        var retries = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            await using var scope = owner.CreateAsyncScope();
            var normalized = index % 2 == 0 ? request : request with { ItemIds = [secondItem, firstItem, firstItem] };
            return await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(normalized);
        }));
        Assert.All(retries, result => Assert.True(result.IsSuccess, result.Error?.Code));
        var accepted = retries[0].Value;
        Assert.All(retries, result => Assert.Equal(accepted, result.Value));
        Assert.Equal(2, accepted.RowCount);
        await using (var scope = owner.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.Equal("pricing.export.request_conflict", (await sender.SendAsync(request with { CalculationState = "Current" })).Error.Code);
            Assert.Equal(accepted, (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value);
        }

        await using var anotherRoot = CreateApplication("another-root", isRoot: true);
        await using var anotherScope = anotherRoot.CreateAsyncScope();
        var otherSender = anotherScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("pricing.export.not_found", (await otherSender.QueryAsync(new GetPricingExport(accepted.ExportId))).Error.Code);
        Assert.Equal("pricing.export.not_found", (await otherSender.SendAsync(new CancelPricingExport(accepted.ExportId, accepted.Version))).Error.Code);
        var other = (await otherSender.SendAsync(request)).Value;
        Assert.NotEqual(accepted.ExportId, other.ExportId);
        Assert.Equal(accepted.SnapshotDigest, other.SnapshotDigest);
        Assert.Equal("another-root", other.Audit!.CreatedBy);
    }

    [PostgresFact]
    public async Task One_worker_owns_the_persisted_generation_lease_and_owner_can_still_cancel_before_publication()
    {
        await using var owner = CreateApplication("export-owner");
        PricingExportStatus accepted;
        await using (var scope = owner.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var item = Guid.NewGuid();
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
            accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        }
        await using var workers = CreateApplication(null);
        var leases = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var scope = workers.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new ClaimPricingExport())).Value;
        }));
        var lease = Assert.Single(leases, value => value is not null)!;
        Assert.Equal(accepted.ExportId, lease.ExportId);
        Assert.Equal(1, lease.Epoch);
        Assert.NotEqual(Guid.Empty, lease.UploadId);
        Assert.True(lease.MaxLeaseUntil > lease.LeaseUntil);
        await using (var scope = workers.CreateAsyncScope())
        {
            Assert.Null((await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new ClaimPricingExport())).Value);
        }
        await using var read = owner.CreateAsyncScope();
        var ownerSender = read.ServiceProvider.GetRequiredService<ISender>();
        var generating = (await ownerSender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        Assert.Equal("Generating", generating.State);
        Assert.Equal(accepted.Version + 1, generating.Version);
        Assert.Equal(lease.Epoch, generating.Epoch);
        Assert.Equal(lease.LeaseUntil, generating.LeaseUntil);
        Assert.Equal("export-owner", generating.Audit!.CreatedBy);
        Assert.Null(generating.Audit.UpdatedBy);
        var canceled = (await ownerSender.SendAsync(new CancelPricingExport(accepted.ExportId, generating.Version))).Value;
        Assert.Equal("Canceled", canceled.State);
        Assert.Equal(generating.Version + 1, canceled.Version);
        Assert.Null(canceled.LeaseUntil);
        await using var stoppedScope = workers.CreateAsyncScope();
        Assert.Null((await stoppedScope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new ClaimPricingExport())).Value);
    }

    [PostgresFact]
    public async Task Renewal_and_expired_takeover_keep_original_upload_identity_and_cannot_revive_old_epoch()
    {
        await using var owner = CreateApplication("export-owner");
        Guid exportId;
        await using (var scope = owner.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var item = Guid.NewGuid();
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
            exportId = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value.ExportId;
        }
        // 允许共用远程库完成一次领取和有效续租，再用实际接管证明到期。
        var policy = new PricingExportOptions { LeaseDuration = TimeSpan.FromSeconds(3), MaxExecutionDuration = TimeSpan.FromSeconds(10) };
        await using var workers = CreateApplication(null, exportOptions: policy);
        await using var workScope = workers.CreateAsyncScope();
        var worker = workScope.ServiceProvider.GetRequiredService<ISender>();
        var first = (await worker.SendAsync(new ClaimPricingExport())).Value!;
        Assert.NotNull(first);
        var renewedResult = await worker.SendAsync(new RenewPricingExport(exportId, first.Epoch));
        Assert.True(renewedResult.IsSuccess, renewedResult.Error?.Code);
        var renewed = renewedResult.Value;
        Assert.Equal(first.Epoch, renewed.Epoch);
        Assert.Equal(first.UploadId, renewed.UploadId);
        Assert.Equal(first.MaxLeaseUntil, renewed.MaxLeaseUntil);
        Assert.True(renewed.LeaseUntil > first.LeaseUntil);

        using var waitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        PricingExportLease? replacement = null;
        while (replacement is null)
        {
            await Task.Delay(50, waitBudget.Token);
            var claimed = await worker.SendAsync(new ClaimPricingExport(), waitBudget.Token);
            Assert.True(claimed.IsSuccess, claimed.Error?.Code);
            replacement = claimed.Value;
        }
        Assert.Equal(first.Epoch + 1, replacement.Epoch);
        Assert.Equal(first.UploadId, replacement.UploadId);
        Assert.Equal("pricing.export.lease_lost", (await worker.SendAsync(new RenewPricingExport(exportId, first.Epoch))).Error.Code);
        await using var check = owner.CreateAsyncScope();
        var status = (await check.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPricingExport(exportId))).Value;
        Assert.Equal(replacement.Epoch, status.Epoch);
        Assert.Equal(replacement.LeaseUntil, status.LeaseUntil);
        Assert.Equal(2, status.Attempts);
    }

    [PostgresFact]
    public async Task Current_worker_selects_one_persistent_publication_and_cancel_then_conflicts_without_replacing_snapshot()
    {
        await using var owner = CreateApplication("export-owner");
        PricingExportStatus accepted;
        await using (var scope = owner.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
            accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        }
        await using var workers = CreateApplication(null);
        await using var workScope = workers.CreateAsyncScope();
        var worker = workScope.ServiceProvider.GetRequiredService<ISender>();
        var lease = (await worker.SendAsync(new ClaimPricingExport())).Value!;
        const string expectedCsv = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        var description = new GeneratedFileDescriptionV1("export-owner", accepted.ExportId, accepted.SnapshotDigest,
            Encoding.UTF8.GetByteCount(expectedCsv), "csv", 1, 1);
        var receipt = new GeneratedFileReceiptV1(42, lease.UploadId, "Staged", accepted.AcceptedAt,
            accepted.AcceptedAt.AddDays(1), accepted.AcceptedAt, description)
        { Producer = "pricing" };
        var selection = new SelectPricingExportPublication(accepted.ExportId, lease.Epoch, Guid.NewGuid(), receipt);
        var chosen = await worker.SendAsync(selection);
        Assert.True(chosen.IsSuccess, chosen.Error?.Code);
        Assert.Equal("Publishing", chosen.Value.State);
        Assert.Equal(42, chosen.Value.FileId);
        Assert.Equal(selection.PublicationId, chosen.Value.PublicationId);
        Assert.Equal(accepted.SnapshotDigest, chosen.Value.SnapshotDigest);
        Assert.Null(chosen.Value.LeaseUntil);
        Assert.Equal(chosen.Value, (await worker.SendAsync(selection)).Value);
        Assert.Equal("pricing.export.selection_conflict", (await worker.SendAsync(selection with { PublicationId = Guid.NewGuid() })).Error.Code);
        await using var ownerScope = owner.CreateAsyncScope();
        var ownerSender = ownerScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("pricing.export.cancel_conflict", (await ownerSender.SendAsync(new CancelPricingExport(accepted.ExportId, chosen.Value.Version))).Error.Code);
        Assert.Equal(chosen.Value, (await ownerSender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value);
        var delivery = (await worker.SendAsync(new ClaimPricingExportPublication())).Value;
        Assert.NotNull(delivery);
        Assert.Equal(selection.PublicationId, delivery.PublicationId);
        Assert.Null((await worker.SendAsync(new ClaimPricingExportPublication())).Value);
        // 合法外部回执的 100ns 尾数必须精确保留，不能被本地 PostgreSQL timestamp 截掉。
        var publishedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10 + 1, TimeSpan.Zero);
        var published = new GeneratedFilePublicationReceiptV1(42, lease.UploadId, selection.PublicationId,
            publishedAt, publishedAt.AddDays(7), description)
        { Producer = "pricing" };
        Assert.Equal("pricing.export.invalid_receipt", (await worker.SendAsync(new CompletePricingExportPublication(delivery,
            published with { FileId = 43 }))).Error.Code);
        var completed = await worker.SendAsync(new CompletePricingExportPublication(delivery, published));
        Assert.True(completed.IsSuccess, completed.Error?.Code);
        Assert.Equal("Succeeded", completed.Value.State);
        Assert.Equal(publishedAt, completed.Value.PublishedAt);
        Assert.Equal(chosen.Value.FileId, completed.Value.FileId);
        Assert.Equal(chosen.Value.SnapshotDigest, completed.Value.SnapshotDigest);
        Assert.Equal(completed.Value, (await worker.SendAsync(new CompletePricingExportPublication(delivery, published))).Value);
        Assert.Null((await worker.SendAsync(new ClaimPricingExport())).Value);
        Assert.Null((await worker.SendAsync(new ClaimPricingExportPublication())).Value);
        Assert.Equal(completed.Value, (await ownerSender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value);
    }

    [PostgresFact]
    public async Task Stopped_publication_is_retried_by_owner_with_the_original_intent_and_no_new_generation()
    {
        var options = new PricingExportOptions { MaxAttempts = 1 };
        await using var owner = CreateApplication("export-owner", exportOptions: options);
        await using var workers = CreateApplication(null, exportOptions: options);
        await using var ownerScope = owner.CreateAsyncScope();
        await using var workScope = workers.CreateAsyncScope();
        var ownerSender = ownerScope.ServiceProvider.GetRequiredService<ISender>();
        var worker = workScope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.True((await ownerSender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await ownerSender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var lease = (await worker.SendAsync(new ClaimPricingExport())).Value!;
        const string csv = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        var description = new GeneratedFileDescriptionV1("export-owner", accepted.ExportId, accepted.SnapshotDigest, Encoding.UTF8.GetByteCount(csv), "csv", 1, 1);
        var staged = new GeneratedFileReceiptV1(42, lease.UploadId, "Staged", accepted.AcceptedAt, accepted.AcceptedAt.AddDays(1), accepted.AcceptedAt, description)
        { Producer = "pricing" };
        var selected = (await worker.SendAsync(new SelectPricingExportPublication(accepted.ExportId, lease.Epoch, Guid.NewGuid(), staged))).Value;
        var first = (await worker.SendAsync(new ClaimPricingExportPublication())).Value!;
        var renewed = await worker.SendAsync(new RenewPricingExportPublication(first.ExportId, first.PublicationId, first.Epoch));
        Assert.True(renewed.IsSuccess, renewed.Error?.Code);
        Assert.Equal(first.MaxLeaseUntil, renewed.Value.MaxLeaseUntil);
        Assert.True((await worker.SendAsync(new FailPricingExportPublication(first, "pricing.export.files_unavailable"))).IsSuccess);
        Assert.Null((await worker.SendAsync(new ClaimPricingExportPublication())).Value);
        Assert.Null((await worker.SendAsync(new ClaimPricingExport())).Value);
        var stopped = (await ownerSender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        Assert.Equal("Publishing", stopped.State);
        Assert.Equal("pricing.export.files_unavailable", stopped.ErrorCode);
        Assert.Equal(selected.PublicationId, stopped.PublicationId);
        await using var other = CreateApplication("another-root", isRoot: true);
        await using var otherScope = other.CreateAsyncScope();
        Assert.Equal("pricing.export.not_found", (await otherScope.ServiceProvider.GetRequiredService<ISender>()
            .SendAsync(new RetryPricingExport(accepted.ExportId, stopped.Version))).Error.Code);
        var recovered = await ownerSender.SendAsync(new RetryPricingExport(accepted.ExportId, stopped.Version));
        Assert.True(recovered.IsSuccess, recovered.Error?.Code);
        Assert.Equal(stopped.PublicationId, recovered.Value.PublicationId);
        Assert.Equal(stopped.SnapshotDigest, recovered.Value.SnapshotDigest);
        Assert.Equal("pricing.export.retry_conflict", (await ownerSender.SendAsync(new RetryPricingExport(accepted.ExportId, stopped.Version))).Error.Code);
        var second = (await worker.SendAsync(new ClaimPricingExportPublication())).Value!;
        Assert.Equal(first.PublicationId, second.PublicationId);
        Assert.Equal(first.Epoch + 1, second.Epoch);
        var publishedAt = DateTimeOffset.UtcNow;
        var published = new GeneratedFilePublicationReceiptV1(42, lease.UploadId, first.PublicationId, publishedAt, publishedAt.AddDays(7), description)
        { Producer = "pricing" };
        Assert.Equal("pricing.export.lease_lost", (await worker.SendAsync(new CompletePricingExportPublication(first, published))).Error.Code);
        var finished = await worker.SendAsync(new CompletePricingExportPublication(second, published));
        Assert.True(finished.IsSuccess, finished.Error?.Code);
        Assert.Equal("Succeeded", finished.Value.State);
        Assert.Equal(stopped.FileId, finished.Value.FileId);
        Assert.Equal("pricing.export.retry_conflict", (await ownerSender.SendAsync(new RetryPricingExport(accepted.ExportId, finished.Value.Version))).Error.Code);
    }

    [PostgresFact]
    public async Task Owner_list_uses_immutable_acceptance_cursor_despite_new_exports_and_rejects_foreign_or_changed_filters()
    {
        await using var owner = CreateApplication("export-owner");
        await using var scope = owner.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var oldest = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var middle = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var newest = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var first = await sender.QueryAsync(new ListPricingExports(Limit: 1));
        Assert.True(first.IsSuccess, first.Error?.Code);
        Assert.Equal(newest.ExportId, Assert.Single(first.Value.Items).ExportId);
        Assert.NotNull(first.Value.NextCursor);
        var later = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var second = (await sender.QueryAsync(new ListPricingExports(Limit: 1, Cursor: first.Value.NextCursor))).Value;
        Assert.Equal(middle.ExportId, Assert.Single(second.Items).ExportId);
        var last = (await sender.QueryAsync(new ListPricingExports(Limit: 1, Cursor: second.NextCursor))).Value;
        Assert.Equal(oldest.ExportId, Assert.Single(last.Items).ExportId);
        Assert.Null(last.NextCursor);
        Assert.True((await sender.SendAsync(new CancelPricingExport(middle.ExportId, middle.Version))).IsSuccess);
        Assert.Equal(middle.ExportId, Assert.Single((await sender.QueryAsync(new ListPricingExports(State: "Canceled"))).Value.Items).ExportId);
        Assert.Equal(later.ExportId, (await sender.QueryAsync(new ListPricingExports())).Value.Items[0].ExportId);
        Assert.Equal("pricing.export.invalid_query", (await sender.QueryAsync(new ListPricingExports(Limit: 201))).Error.Code);
        Assert.Equal("pricing.export.invalid_query", (await sender.QueryAsync(new ListPricingExports(State: "Canceled", Cursor: first.Value.NextCursor))).Error.Code);
        await using var other = CreateApplication("other-root", isRoot: true);
        await using var otherScope = other.CreateAsyncScope();
        var otherSender = otherScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Empty((await otherSender.QueryAsync(new ListPricingExports())).Value.Items);
        Assert.Equal("pricing.export.invalid_query", (await otherSender.QueryAsync(new ListPricingExports(Cursor: first.Value.NextCursor))).Error.Code);
    }

    [PostgresFact]
    public async Task Concurrent_cancel_and_publication_selection_have_one_durable_winner()
    {
        await using var owner = CreateApplication("export-owner");
        await using var workers = CreateApplication(null);
        await using var seed = owner.CreateAsyncScope();
        var sender = seed.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        await using var claiming = workers.CreateAsyncScope();
        var lease = (await claiming.ServiceProvider.GetRequiredService<ISender>().SendAsync(new ClaimPricingExport())).Value!;
        var generating = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        const string csv = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        var staged = new GeneratedFileReceiptV1(42, lease.UploadId, "Staged", accepted.AcceptedAt, accepted.AcceptedAt.AddDays(1), accepted.AcceptedAt,
            new GeneratedFileDescriptionV1("export-owner", accepted.ExportId, accepted.SnapshotDigest, Encoding.UTF8.GetByteCount(csv), "csv", 1, 1))
        { Producer = "pricing" };
        await using var selecting = workers.CreateAsyncScope();
        await using var canceling = owner.CreateAsyncScope();
        var select = selecting.ServiceProvider.GetRequiredService<ISender>().SendAsync(new SelectPricingExportPublication(accepted.ExportId, lease.Epoch, Guid.NewGuid(), staged));
        var cancel = canceling.ServiceProvider.GetRequiredService<ISender>().SendAsync(new CancelPricingExport(accepted.ExportId, generating.Version));
        var results = await Task.WhenAll(select, cancel);
        Assert.Single(results, result => result.IsSuccess);
        var status = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        Assert.Equal(accepted.SnapshotDigest, status.SnapshotDigest);
        if (results[0].IsSuccess)
        {
            Assert.Equal("pricing.export.cancel_conflict", results[1].Error.Code);
            Assert.Equal("Publishing", status.State);
            Assert.Equal(results[0].Value.PublicationId, status.PublicationId);
        }
        else
        {
            Assert.Equal("pricing.export.lease_lost", results[0].Error.Code);
            Assert.Equal("Canceled", status.State);
            Assert.Null(status.PublicationId);
            Assert.Null((await claiming.ServiceProvider.GetRequiredService<ISender>().SendAsync(new ClaimPricingExportPublication())).Value);
        }
    }
}
