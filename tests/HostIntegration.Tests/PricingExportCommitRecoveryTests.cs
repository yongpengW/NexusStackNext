using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingExportCommitRecoveryTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Server_committed_acceptance_with_lost_wire_reply_replays_original_snapshot_after_quote_change()
    {
        await using var database = await databases.CreateAsync("pricing");
        await using var owner = Application(database.ConnectionString, "export-owner");
        await using var scope = owner.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var request = new AcceptPricingExport(Guid.NewGuid(), [item]);
        await using var fault = new PostgresCommitReplyFault(database.ConnectionString);
        await using var interrupted = Application(fault.ConnectionString, "export-owner");
        await using var interruptedScope = interrupted.CreateAsyncScope();
        fault.Arm();
        var uncertain = await interruptedScope.ServiceProvider.GetRequiredService<ISender>().SendAsync(request);
        await fault.WaitForDroppedCommitAsync();
        Assert.Equal("pricing.export.unavailable", uncertain.Error.Code);
        var original = Assert.Single((await sender.QueryAsync(new ListPricingExports())).Value.Items);
        Assert.Equal("Queued", original.State);
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 1, 120m, 0.2m))).IsSuccess);
        Assert.Equal(original, (await sender.SendAsync(request)).Value);
        Assert.Equal(original, (await sender.QueryAsync(new GetPricingExport(original.ExportId))).Value);
        Assert.Equal(original.ExportId, Assert.Single((await sender.QueryAsync(new ListPricingExports())).Value.Items).ExportId);
        var fresh = (await sender.SendAsync(request with { RequestId = Guid.NewGuid() })).Value;
        Assert.NotEqual(original.SnapshotDigest, fresh.SnapshotDigest);
    }

    [PostgresFact]
    public async Task Server_committed_completion_with_lost_wire_reply_recovers_original_file_and_publication_without_new_generation()
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "export-test-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await UserLifecycleHttpTests.LoginAsync(host.Client, "journey-root", "export-test-password");
        var ownerId = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var transport = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        var files = transport.GetRequiredService<IExportFiles>();
        await using var owner = Application(pricing.ConnectionString, ownerId, files);
        await using var scope = owner.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var generation = (await sender.SendAsync(new ClaimPricingExport())).Value!;
        var selected = (await sender.SendAsync(new GeneratePricingExport(generation))).Value;
        var delivery = (await sender.SendAsync(new ClaimPricingExportPublication())).Value!;
        var published = await files.PublishAsync(new ExportFilePublication(generation.UploadId, selected.FileId!.Value, selected.PublicationId!.Value,
            new(ownerId, accepted.ExportId, accepted.SnapshotDigest, accepted.SnapshotLength, "csv", 1, 1)));
        Assert.True(published.IsSuccess, published.Error?.Code);
        await using var fault = new PostgresCommitReplyFault(pricing.ConnectionString);
        await using var interrupted = Application(fault.ConnectionString, null);
        await using var interruptedScope = interrupted.CreateAsyncScope();
        fault.Arm();
        var uncertain = await interruptedScope.ServiceProvider.GetRequiredService<ISender>()
            .SendAsync(new CompletePricingExportPublication(delivery, published.Value));
        await fault.WaitForDroppedCommitAsync();
        Assert.Equal("pricing.export.unavailable", uncertain.Error.Code);
        var completed = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        Assert.Equal("Succeeded", completed.State);
        Assert.Equal("Delivered", completed.Delivery!.State);
        Assert.Equal(selected.FileId, completed.FileId);
        Assert.Equal(selected.PublicationId, completed.PublicationId);
        Assert.Equal(published.Value.PublishedAt, completed.PublishedAt);
        Assert.Equal(published.Value.ExpiresAt, completed.ExpiresAt);
        Assert.Equal(completed, (await sender.SendAsync(new CompletePricingExportPublication(delivery, published.Value))).Value);
        Assert.Equal(completed, (await sender.SendAsync(new PublishPricingExport(delivery))).Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingExport())).Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingExportPublication())).Value);
        using var downloaded = await host.Client.GetAsync(new Uri($"/api/files/{completed.FileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        const string expected = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await downloaded.Content.ReadAsByteArrayAsync());
    }

    private static ServiceProvider Application(string connectionString, string? owner, IExportFiles? files = null)
    {
        var services = new ServiceCollection().AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(owner));
        services.AddPricingPostgres(connectionString).AddPricingExportPersistence();
        if (files is not null) { services.AddSingleton(files).AddPricingExportProcessing(); }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [PostgresFact]
    public Task Late_original_publication_receipt_after_owner_deletion_records_success_history_without_reviving_bytes() => VerifyLateReceiptAsync(expired: false);

    [PostgresFact]
    public Task Late_original_publication_receipt_after_expiry_records_success_history_without_extending_lifetime() => VerifyLateReceiptAsync(expired: true);

    private async Task VerifyLateReceiptAsync(bool expired)
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        var settings = new Dictionary<string, string>(certificates.Settings)
        { ["Files__Generated__DownloadLifetimeSeconds"] = expired ? "2" : "604800", ["Files__Cleanup__IntervalSeconds"] = "3600" };
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "export-test-password", settings: settings,
            listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await UserLifecycleHttpTests.LoginAsync(host.Client, "journey-root", "export-test-password");
        var ownerId = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var transport = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        var files = transport.GetRequiredService<IExportFiles>();
        await using var owner = Application(pricing.ConnectionString, ownerId, files);
        await using var scope = owner.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var request = new AcceptPricingExport(Guid.NewGuid(), [item]);
        var accepted = (await sender.SendAsync(request)).Value;
        var generation = (await sender.SendAsync(new ClaimPricingExport())).Value!;
        var selected = (await sender.SendAsync(new GeneratePricingExport(generation))).Value;
        var delivery = (await sender.SendAsync(new ClaimPricingExportPublication())).Value!;
        var published = (await files.PublishAsync(new ExportFilePublication(generation.UploadId, selected.FileId!.Value, selected.PublicationId!.Value,
            new(ownerId, accepted.ExportId, accepted.SnapshotDigest, accepted.SnapshotLength, "csv", 1, 1)))).Value;
        if (!expired)
        {
            using var deleted = await host.Client.DeleteAsync(new Uri($"/api/files/{published.FileId}", UriKind.Relative));
            Assert.Contains(deleted.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Accepted });
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var unavailable = await host.Client.GetAsync(new Uri($"/api/files/{published.FileId}", UriKind.Relative), budget.Token);
            if (unavailable.StatusCode == HttpStatusCode.NotFound) { break; }
            Assert.Equal(HttpStatusCode.OK, unavailable.StatusCode);
            await Task.Delay(50, budget.Token);
        }
        var completed = (await sender.SendAsync(new CompletePricingExportPublication(delivery, published))).Value;
        Assert.Equal("Succeeded", completed.State);
        Assert.Equal(published.FileId, completed.FileId);
        Assert.Equal(published.PublicationId, completed.PublicationId);
        Assert.Equal(published.PublishedAt, completed.PublishedAt);
        Assert.Equal(published.ExpiresAt, completed.ExpiresAt);
        var artifact = (await sender.QueryAsync(new GetPricingExportArtifact(accepted.ExportId))).Value;
        Assert.Equal(expired ? "Expired" : "Deleted", artifact.State);
        Assert.Null(artifact.DownloadPath);
        Assert.Equal(published.PublishedAt, artifact.PublishedAt);
        Assert.Equal(published.ExpiresAt, artifact.ExpiresAt);
        Assert.Equal(completed, (await sender.SendAsync(request)).Value);
        Assert.Equal(completed, (await sender.SendAsync(new CompletePricingExportPublication(delivery, published))).Value);
        Assert.Equal("pricing.export.retry_conflict", (await sender.SendAsync(new RetryPricingExport(accepted.ExportId, completed.Version))).Error.Code);
        Assert.Null((await sender.SendAsync(new ClaimPricingExport())).Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingExportPublication())).Value);
        var replay = (await files.PublishAsync(new ExportFilePublication(generation.UploadId, published.FileId, published.PublicationId, published.Description))).Value;
        Assert.Equal(published, replay);
        using var gone = await host.Client.GetAsync(new Uri($"/api/files/{published.FileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }
}
