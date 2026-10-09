using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingExportResourceRecoveryTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Unavailable_configured_temp_storage_leaves_no_selected_file_and_releases_slot_so_original_lease_can_recover()
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "export-test-password", settings: certificates.Settings,
            listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await UserLifecycleHttpTests.LoginAsync(host.Client, "journey-root", "export-test-password");
        var ownerId = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var transport = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        var files = transport.GetRequiredService<IExportFiles>();
        using var storage = new JourneyFileStorage();
        Directory.CreateDirectory(storage.Root);
        var blocked = Path.Combine(storage.Root, "generation");
        await File.WriteAllTextAsync(blocked, "Owned fixture blocks directory creation.");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TemporaryDirectory"] = blocked }).Build();
        var options = configuration.Get<PricingExportOptions>()!;
        await using var app = Application(pricing.ConnectionString, ownerId, files, options);
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var lease = (await sender.SendAsync(new ClaimPricingExport())).Value!;
        var unavailable = await sender.SendAsync(new GeneratePricingExport(lease));
        Assert.Equal("pricing.export.storage_unavailable", unavailable.Error.Code);
        var stillGenerating = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        Assert.Equal("Generating", stillGenerating.State);
        Assert.Null(stillGenerating.FileId);
        Assert.Equal(0, stillGenerating.ConfirmedGeneratedRows);
        // Restore only the fixture's file. A second call through the same container must own the released slot.
        File.Delete(blocked);
        var recovered = await sender.SendAsync(new GeneratePricingExport(lease));
        Assert.True(recovered.IsSuccess, recovered.Error?.Code);
        Assert.Equal("Publishing", recovered.Value.State);
        Assert.Equal(accepted.SnapshotDigest, recovered.Value.SnapshotDigest);
        Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.csv.tmp", SearchOption.AllDirectories));
    }

    private static ServiceProvider Application(string connectionString, string owner, IExportFiles files, PricingExportOptions options)
    {
        var services = new ServiceCollection().AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(owner));
        services.AddPricingPostgres(connectionString).AddPricingExportPersistence(options);
        services.AddSingleton(files).AddPricingExportProcessing();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [PostgresFact]
    public async Task Actual_generation_output_limit_releases_temp_file_and_slot_without_selecting_an_artifact()
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "export-test-password", settings: certificates.Settings,
            listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await UserLifecycleHttpTests.LoginAsync(host.Client, "journey-root", "export-test-password");
        var ownerId = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var transport = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        using var storage = new JourneyFileStorage();
        await using var app = Application(pricing.ConnectionString, ownerId, transport.GetRequiredService<IExportFiles>(),
            new PricingExportOptions { MaxOutputBytes = 1, TemporaryDirectory = storage.Root });
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var lease = (await sender.SendAsync(new ClaimPricingExport())).Value!;
        Assert.Equal("pricing.export.output_limit", (await sender.SendAsync(new GeneratePricingExport(lease))).Error.Code);
        Assert.Equal("pricing.export.output_limit", (await sender.SendAsync(new GeneratePricingExport(lease))).Error.Code);
        var status = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        Assert.Equal("Generating", status.State);
        Assert.Null(status.FileId);
        Assert.Null(status.PublicationId);
        Assert.Equal(0, status.ConfirmedGeneratedRows);
        Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.csv.tmp", SearchOption.AllDirectories));
        Assert.True((await sender.SendAsync(new CancelPricingExport(accepted.ExportId, status.Version))).IsSuccess);
    }

    [PostgresFact]
    public async Task Paused_upload_holds_one_generation_slot_but_no_transaction_and_caller_cancellation_releases_resources_for_other_work()
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "export-test-password", settings: certificates.Settings,
            listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await UserLifecycleHttpTests.LoginAsync(host.Client, "journey-root", "export-test-password");
        var ownerId = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var fault = await GeneratedFileReplyFault.StartAsync(certificates, host.Client.BaseAddress!, GeneratedFileFaultPoint.BeforeContent);
        await using var transport = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(fault.BaseAddress), "Testing").BuildServiceProvider();
        using var storage = new JourneyFileStorage();
        await using var app = Application(pricing.ConnectionString, ownerId, transport.GetRequiredService<IExportFiles>(),
            new PricingExportOptions { TemporaryDirectory = storage.Root });
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var first = (await sender.SendAsync(new ClaimPricingExport())).Value!;
        var another = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var second = (await sender.SendAsync(new ClaimPricingExport())).Value!;
        Assert.Equal(another.ExportId, second.ExportId);
        await using var generatingScope = app.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();
        var generating = generatingScope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new GeneratePricingExport(first), cancellation.Token);
        try
        {
            await fault.WaitForFaultAsync();
            using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
            using var pending = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{first.UploadId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            var pendingFile = (await pending.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
            using var blocked = await host.Client.GetAsync(new Uri($"/api/files/{pendingFile}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
            Assert.Equal("pricing.export.generator_busy", (await sender.SendAsync(new GeneratePricingExport(second))).Error.Code);
            var current = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
            // A user command must commit while the real Files HTTPS request is still paused.
            var canceled = await sender.SendAsync(new CancelPricingExport(accepted.ExportId, current.Version)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Canceled", canceled.Value.State);
            Assert.Null(canceled.Value.FileId);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await generating);
        }
        finally
        {
            await cancellation.CancelAsync();
            fault.Release();
            await ((Task)generating).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.csv.tmp", SearchOption.AllDirectories));
        Assert.Equal("Canceled", (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value.State);
        var resumed = await sender.SendAsync(new GeneratePricingExport(second));
        Assert.True(resumed.IsSuccess, resumed.Error?.Code);
        Assert.Equal("Publishing", resumed.Value.State);
        Assert.Equal(another.ExportId, resumed.Value.ExportId);
        Assert.Equal(another.SnapshotDigest, resumed.Value.SnapshotDigest);
        Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.csv.tmp", SearchOption.AllDirectories));
    }

    [PostgresFact]
    public Task Concurrent_cancel_and_real_https_candidate_selection_leave_exactly_one_durable_winner() => VerifyCandidateCompetitionAsync(CandidateOrder.Concurrent);

    [PostgresFact]
    public Task Cancel_wins_before_real_candidate_selection_and_keeps_staged_bytes_undownloadable() => VerifyCandidateCompetitionAsync(CandidateOrder.CancellationFirst);

    [PostgresFact]
    public Task Real_candidate_selection_wins_before_cancel_and_publishes_one_exact_artifact() => VerifyCandidateCompetitionAsync(CandidateOrder.SelectionFirst);

    private enum CandidateOrder { Concurrent, CancellationFirst, SelectionFirst }

    private async Task VerifyCandidateCompetitionAsync(CandidateOrder order)
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "export-test-password", settings: certificates.Settings,
            listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await UserLifecycleHttpTests.LoginAsync(host.Client, "journey-root", "export-test-password");
        var ownerId = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var transport = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        var files = transport.GetRequiredService<IExportFiles>();
        await using var app = Application(pricing.ConnectionString, ownerId, files, new PricingExportOptions());
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var request = new AcceptPricingExport(Guid.NewGuid(), [item]);
        var accepted = (await sender.SendAsync(request)).Value;
        var lease = (await sender.SendAsync(new ClaimPricingExport())).Value!;
        var generating = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        const string csv = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        var bytes = Encoding.UTF8.GetBytes(csv);
        using var content = new MemoryStream(bytes);
        var staged = await files.StageAsync(new ExportFileUpload(lease.UploadId,
            new(ownerId, accepted.ExportId, accepted.SnapshotDigest, bytes.Length, "csv", 1, 1)), content);
        Assert.True(staged.IsSuccess, staged.Error?.Code);
        Assert.Equal("Staged", staged.Value.Stage);
        using (var hidden = await host.Client.GetAsync(new Uri($"/api/files/{staged.Value.FileId}", UriKind.Relative)))
        { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        await using var selecting = app.CreateAsyncScope();
        await using var canceling = app.CreateAsyncScope();
        var selector = selecting.ServiceProvider.GetRequiredService<ISender>();
        var canceler = canceling.ServiceProvider.GetRequiredService<ISender>();
        var selection = new SelectPricingExportPublication(accepted.ExportId, lease.Epoch, Guid.NewGuid(), staged.Value);
        var cancellation = new CancelPricingExport(accepted.ExportId, generating.Version);
        Result<PricingExportStatus> selected;
        Result<PricingExportStatus> canceled;
        if (order == CandidateOrder.Concurrent)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var select = Task.Run(async () => { await start.Task; return await selector.SendAsync(selection); });
            var cancel = Task.Run(async () => { await start.Task; return await canceler.SendAsync(cancellation); });
            start.SetResult();
            var results = await Task.WhenAll(select, cancel).WaitAsync(TimeSpan.FromSeconds(10));
            selected = results[0];
            canceled = results[1];
        }
        else if (order == CandidateOrder.CancellationFirst)
        {
            canceled = await canceler.SendAsync(cancellation);
            selected = await selector.SendAsync(selection);
            Assert.True(canceled.IsSuccess, canceled.Error?.Code);
        }
        else
        {
            selected = await selector.SendAsync(selection);
            canceled = await canceler.SendAsync(cancellation);
            Assert.True(selected.IsSuccess, selected.Error?.Code);
        }
        Assert.NotEqual(selected.IsSuccess, canceled.IsSuccess);
        var status = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        Assert.Equal(accepted.SnapshotDigest, status.SnapshotDigest);
        if (selected.IsSuccess)
        {
            Assert.Equal("pricing.export.cancel_conflict", canceled.Error?.Code);
            Assert.Equal("Publishing", status.State);
            Assert.Equal(staged.Value.FileId, status.FileId);
            Assert.Equal(selection.PublicationId, status.PublicationId);
            var delivery = (await sender.SendAsync(new ClaimPricingExportPublication())).Value!;
            var published = await sender.SendAsync(new PublishPricingExport(delivery));
            Assert.True(published.IsSuccess, published.Error?.Code);
            Assert.Equal("Succeeded", published.Value.State);
            Assert.Equal(staged.Value.FileId, published.Value.FileId);
            Assert.Equal(selection.PublicationId, published.Value.PublicationId);
            Assert.Equal(published.Value, (await sender.SendAsync(new PublishPricingExport(delivery))).Value);
            using var downloaded = await host.Client.GetAsync(new Uri($"/api/files/{staged.Value.FileId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
            Assert.Equal(bytes, await downloaded.Content.ReadAsByteArrayAsync());
        }
        else
        {
            Assert.Equal("pricing.export.lease_lost", selected.Error?.Code);
            Assert.Equal("Canceled", status.State);
            Assert.Null(status.FileId);
            Assert.Null(status.PublicationId);
            Assert.Equal("pricing.export.lease_lost", (await sender.SendAsync(selection)).Error.Code);
            content.Position = 0;
            var replay = await files.StageAsync(new ExportFileUpload(lease.UploadId, staged.Value.Description), content);
            Assert.Equal(staged.Value, replay.Value);
            using var hidden = await host.Client.GetAsync(new Uri($"/api/files/{staged.Value.FileId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        Assert.Null((await sender.SendAsync(new ClaimPricingExportPublication())).Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingExport())).Value);
        Assert.Equal((await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value, (await sender.SendAsync(request)).Value);
    }
}
