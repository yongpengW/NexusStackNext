using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingExportRecoveryTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public Task Lost_upload_response_resumes_original_candidate_and_frozen_bytes() => VerifyLostReplyAsync("upload");

    [PostgresFact]
    public Task Lost_publication_response_recovers_original_history_without_second_generation() => VerifyLostReplyAsync("publication");

    [PostgresFact]
    public async Task Restarted_generation_and_publication_are_system_operations_linked_to_the_original_accepting_user()
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var transport = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        var files = transport.GetRequiredService<IExportFiles>();
        Guid acceptedOperation;
        PricingExportStatus accepted;
        await using (var producer = ObservedApplication(owner))
        await using (var scope = producer.Services.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var item = Guid.NewGuid();
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
            var request = new AcceptPricingExport(Guid.NewGuid(), [item]);
            accepted = (await sender.SendAsync(request)).Value;
            var finished = Assert.Single(await ObservationsAsync(scope.ServiceProvider), observation => observation.Phase == "finished"
                && observation.Metadata?.Action == "command.AcceptPricingExport");
            acceptedOperation = finished.OperationId;
            Assert.Equal("accepted", finished.Outcome);
            Assert.Equal(owner, finished.ActorId);
            Assert.Equal(accepted, (await sender.SendAsync(request)).Value);
        }
        await using var restarted = ObservedApplication(null);
        await using var workerScope = restarted.Services.CreateAsyncScope();
        var worker = workerScope.ServiceProvider.GetRequiredService<ISender>();
        var lease = (await worker.SendAsync(new ClaimPricingExport())).Value!;
        Assert.True((await worker.SendAsync(new GeneratePricingExport(lease))).IsSuccess);
        var publication = (await worker.SendAsync(new ClaimPricingExportPublication())).Value!;
        Assert.True((await worker.SendAsync(new PublishPricingExport(publication))).IsSuccess);
        var observations = await ObservationsAsync(workerScope.ServiceProvider);
        Assert.Equal(4, observations.Length);
        Assert.All(observations, observation =>
        {
            Assert.Null(observation.ActorId);
            Assert.Equal(acceptedOperation, observation.Metadata!.RootOperationId);
            Assert.Equal(acceptedOperation, observation.Metadata.ParentOperationId);
            Assert.Equal(owner, observation.Metadata.InitiatorId);
        });
        var generated = Assert.Single(observations, observation => observation.Phase == "finished" && observation.Metadata?.Action == "pricing.export.generate");
        Assert.Equal(accepted.ExportId, generated.Metadata!.TaskId);
        Assert.Equal(lease.Epoch, generated.Metadata.TaskEpoch);
        Assert.Equal("completed", generated.Outcome);
        Assert.Equal("completed", Assert.Single(observations, observation => observation.Phase == "finished" && observation.Metadata?.Action == "pricing.export.publish").Outcome);

        WebApplication ObservedApplication(string? actor)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OperationJournal:Storage:Provider"] = "Memory" });
            builder.Services.AddNexusStackApplication();
            builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "pricing");
            builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(actor));
            builder.Services.AddPricingPostgres(pricing.ConnectionString).AddPricingExportPersistence();
            builder.Services.AddSingleton(files).AddPricingExportProcessing();
            return builder.Build();
        }
        static async Task<OperationObservedV1[]> ObservationsAsync(IServiceProvider services)
        {
            var journal = services.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            return (await journal.ReadPendingAsync(30, DateTimeOffset.UtcNow)).Select(message =>
                new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload)).ToArray();
        }
    }

    private async Task VerifyLostReplyAsync(string phase)
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var ownerId = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var transport = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        var files = transport.GetRequiredService<IExportFiles>();
        var options = new PricingExportOptions { MaxAttempts = 1 };
        await using var owner = Application(ownerId);
        await using var owners = owner.CreateAsyncScope();
        var sender = owners.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        await using var interrupted = Application(null, new LostReplyFiles(files, phase));
        await using var interruptedScope = interrupted.CreateAsyncScope();
        var worker = interruptedScope.ServiceProvider.GetRequiredService<ISender>();
        var first = (await worker.SendAsync(new ClaimPricingExport())).Value!;
        var generated = await worker.SendAsync(new GeneratePricingExport(first));
        PricingExportPublicationLease? oldPublication = null;
        if (phase == "upload")
        {
            Assert.Equal("pricing.export.files_unavailable", generated.Error.Code);
            Assert.True((await worker.SendAsync(new FailPricingExport(first.ExportId, first.Epoch, generated.Error.Code))).IsSuccess);
        }
        else
        {
            Assert.True(generated.IsSuccess, generated.Error?.Code);
            oldPublication = (await worker.SendAsync(new ClaimPricingExportPublication())).Value!;
            var lost = await worker.SendAsync(new PublishPricingExport(oldPublication));
            Assert.Equal("pricing.export.files_unavailable", lost.Error.Code);
            Assert.True((await worker.SendAsync(new FailPricingExportPublication(oldPublication, lost.Error.Code))).IsSuccess);
        }
        var stopped = (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value;
        Assert.Equal(phase == "upload" ? "Failed" : "Publishing", stopped.State);
        if (phase == "publication") { Assert.Equal("Stopped", stopped.Delivery!.State); }
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 1, 120m, 0.2m))).IsSuccess);
        Assert.True((await sender.SendAsync(new RetryPricingExport(accepted.ExportId, stopped.Version))).IsSuccess);
        // 新容器代表新的执行实例；唯一状态来源是原数据库与原 Files 协议。
        await using var restarted = Application(null, files);
        await using var resumed = restarted.CreateAsyncScope();
        var current = resumed.ServiceProvider.GetRequiredService<ISender>();
        if (phase == "upload")
        {
            var next = (await current.SendAsync(new ClaimPricingExport())).Value!;
            Assert.Equal(first.UploadId, next.UploadId);
            Assert.True((await current.SendAsync(new GeneratePricingExport(next))).IsSuccess);
        }
        else { Assert.Null((await current.SendAsync(new ClaimPricingExport())).Value); }
        var publication = (await current.SendAsync(new ClaimPricingExportPublication())).Value!;
        if (oldPublication is not null) { Assert.Equal(oldPublication.PublicationId, publication.PublicationId); }
        var completed = await current.SendAsync(new PublishPricingExport(publication));
        Assert.True(completed.IsSuccess, completed.Error?.Code);
        Assert.Equal("Succeeded", completed.Value.State);
        Assert.Equal(accepted.SnapshotDigest, completed.Value.SnapshotDigest);
        if (phase == "publication") { Assert.Equal(stopped.FileId, completed.Value.FileId); }
        using var downloaded = await host.Client.GetAsync(new Uri($"/api/files/{completed.Value.FileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        const string expected = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await downloaded.Content.ReadAsByteArrayAsync());

        ServiceProvider Application(string? user, IExportFiles? adapter = null)
        {
            var services = new ServiceCollection().AddNexusStackApplication();
            services.AddSingleton<ICurrentUser>(new FixedCurrentUser(user));
            services.AddPricingPostgres(pricing.ConnectionString).AddPricingExportPersistence(options);
            if (adapter is not null) { services.AddSingleton(adapter).AddPricingExportProcessing(); }
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }
    }

    // 在公开文件端口丢弃已经实际提交的回执；不伪造 Files 状态，也不检查调用次数。
    private sealed class LostReplyFiles(IExportFiles inner, string phase) : IExportFiles
    {
        public async Task<Result<GeneratedFileReceiptV1>> StageAsync(ExportFileUpload upload, Stream content, CancellationToken cancellationToken = default)
        {
            var committed = await inner.StageAsync(upload, content, cancellationToken);
            return committed.IsSuccess && phase == "upload" ? Result.Failure<GeneratedFileReceiptV1>(Lost()) : committed;
        }
        public async Task<Result<GeneratedFilePublicationReceiptV1>> PublishAsync(ExportFilePublication publication, CancellationToken cancellationToken = default)
        {
            var committed = await inner.PublishAsync(publication, cancellationToken);
            return committed.IsSuccess && phase == "publication" ? Result.Failure<GeneratedFilePublicationReceiptV1>(Lost()) : committed;
        }
        public Task<Result<GeneratedFileAvailabilityV1>> AvailabilityAsync(Guid uploadId, long fileId, CancellationToken cancellationToken = default)
            => inner.AvailabilityAsync(uploadId, fileId, cancellationToken);
        private static Error Lost() => new("pricing.export.files_unavailable", "已提交回执未送达调用方。");
    }
}
