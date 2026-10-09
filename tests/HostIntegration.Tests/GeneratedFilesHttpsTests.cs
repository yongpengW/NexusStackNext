using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class GeneratedFilesHttpsTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Private_xlsx_candidate_is_published_as_an_excel_attachment_with_private_cache_policy()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var services = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        var files = services.GetRequiredService<IExportFiles>();
        using var content = new MemoryStream();
        await PricingXlsxV1.WriteAsync([new(new NexusStackNext.Pricing.Domain.PriceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            long.MaxValue, 80m, 0.2m, 1, 0, 0, null)], content);
        var expected = content.ToArray();
        var description = new GeneratedFileDescriptionV1(owner, Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(expected)), expected.Length, "xlsx", 1, 1);
        var upload = new ExportFileUpload(Guid.NewGuid(), description);
        var staged = await files.StageAsync(upload, content);
        Assert.True(staged.IsSuccess, staged.Error?.Code);
        using (var hidden = await host.Client.GetAsync(new Uri($"/api/files/{staged.Value.FileId}", UriKind.Relative)))
        { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        var publication = new ExportFilePublication(upload.UploadId, staged.Value.FileId, Guid.NewGuid(), description);
        Assert.True((await files.PublishAsync(publication)).IsSuccess);
        using var download = await host.Client.GetAsync(new Uri($"/api/files/{staged.Value.FileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("export.xlsx", download.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.True(download.Headers.CacheControl!.Private);
        Assert.True(download.Headers.CacheControl.NoStore);
        Assert.Equal(expected, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(staged.Value, (await files.StageAsync(upload, content)).Value);
    }

    [PostgresFact]
    public async Task Pricing_https_adapter_rejects_server_outside_its_explicit_private_trust_roots()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var unrelated = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        var options = certificates.PricingClientOptions(host.Client.BaseAddress!);
        options.RootCertificatePaths = [unrelated.Settings["Files__Producer__RootCertificatePaths__0"]];
        await using var services = new ServiceCollection().AddPricingExportFiles(options, "Testing").BuildServiceProvider();
        using var content = new MemoryStream([1, 2, 3]);
        var attempted = await services.GetRequiredService<IExportFiles>().StageAsync(new ExportFileUpload(Guid.NewGuid(), Description("probe-owner")), content);
        Assert.Equal("pricing.export.files_unavailable", attempted.Error.Code);
    }

    [PostgresFact]
    public Task Pricing_export_generates_frozen_csv_through_https_and_completes_original_publication() => VerifyFrozenExportAsync("csv");

    [PostgresFact]
    public Task Pricing_export_generates_frozen_xlsx_through_https_and_completes_original_publication() => VerifyFrozenExportAsync("xlsx");

    private async Task VerifyFrozenExportAsync(string format)
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        var services = new ServiceCollection().AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(owner));
        services.AddPricingPostgres(pricing.ConnectionString).AddPricingExportPersistence();
        await using var ownerApp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        PricingExportStatus accepted;
        await using (var scope = ownerApp.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
            accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item], Format: format))).Value;
            Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 1, 120m, 0.2m))).IsSuccess);
        }
        var workerServices = new ServiceCollection().AddNexusStackApplication();
        workerServices.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        workerServices.AddPricingPostgres(pricing.ConnectionString).AddPricingExportPersistence().AddPricingExportProcessing()
            .AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing");
        await using var workers = workerServices.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var work = workers.CreateAsyncScope();
        var worker = work.ServiceProvider.GetRequiredService<ISender>();
        var generation = (await worker.SendAsync(new ClaimPricingExport())).Value!;
        var generated = await worker.SendAsync(new GeneratePricingExport(generation));
        Assert.True(generated.IsSuccess, generated.Error?.Code);
        Assert.Equal("Publishing", generated.Value.State);
        var delivery = (await worker.SendAsync(new ClaimPricingExportPublication())).Value!;
        var completed = await worker.SendAsync(new PublishPricingExport(delivery));
        Assert.True(completed.IsSuccess, completed.Error?.Code);
        Assert.Equal("Succeeded", completed.Value.State);
        Assert.Equal(accepted.SnapshotDigest, completed.Value.SnapshotDigest);
        Assert.Null(completed.Value.Audit!.UpdatedBy);
        using var downloaded = await host.Client.GetAsync(new Uri($"/api/files/{completed.Value.FileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        const string expected = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        var bytes = await downloaded.Content.ReadAsByteArrayAsync();
        if (format == "csv") { Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes); }
        else
        {
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", downloaded.Content.Headers.ContentType!.MediaType);
            PricingWorkbookAssertions.FrozenPendingQuote(bytes);
        }
        Assert.Equal(completed.Value, (await worker.SendAsync(new PublishPricingExport(delivery))).Value);
    }

    [PostgresFact]
    public async Task Pricing_https_adapter_seals_original_bytes_and_recovers_same_publication_after_content_deletion()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        await using var services = new ServiceCollection().AddPricingExportFiles(certificates.PricingClientOptions(host.Client.BaseAddress!), "Testing").BuildServiceProvider();
        var files = services.GetRequiredService<IExportFiles>();
        var upload = new ExportFileUpload(Guid.NewGuid(), Description(owner));
        using var content = new MemoryStream([1, 2, 3]);
        var staged = await files.StageAsync(upload, content);
        Assert.True(staged.IsSuccess, staged.Error?.Code);
        Assert.Equal("pricing", staged.Value.Producer);
        Assert.Equal(upload.Description, staged.Value.Description);
        Assert.Equal("Staged", staged.Value.Stage);
        Assert.True(content.CanRead);
        Assert.Equal(staged.Value, (await files.StageAsync(upload, content)).Value);
        var intent = new ExportFilePublication(upload.UploadId, staged.Value.FileId, Guid.NewGuid(), upload.Description);
        var published = await files.PublishAsync(intent);
        Assert.True(published.IsSuccess, published.Error?.Code);
        Assert.Equal("pricing", published.Value.Producer);
        Assert.Equal(intent.PublicationId, published.Value.PublicationId);
        Assert.Equal("Available", (await files.AvailabilityAsync(upload.UploadId, staged.Value.FileId)).Value.State);
        using var download = await host.Client.GetAsync(new Uri($"/api/files/{staged.Value.FileId}", UriKind.Relative));
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
        await DeleteAndWaitAsync(host.Client, staged.Value.FileId);
        Assert.Equal("Deleted", (await files.AvailabilityAsync(upload.UploadId, staged.Value.FileId)).Value.State);
        Assert.Equal(published.Value, (await files.PublishAsync(intent)).Value);
    }

    [PostgresFact]
    public async Task Producer_identity_is_persisted_in_both_upload_and_publication_receipts()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.NewGuid();
        await StageAsync(producer, uploadId, owner);
        using var uploaded = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var staged = await uploaded.Content.ReadApiDataAsync();
        Assert.True(staged.TryGetProperty("producer", out var producerIdentity), "Upload receipt must identify the authenticated producer.");
        Assert.Equal("pricing", producerIdentity.GetString());
        var publicationId = Guid.NewGuid();
        using var published = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative),
            new GeneratedFilePublicationV1(publicationId));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var publication = await published.Content.ReadApiDataAsync();
        Assert.Equal("pricing", publication.GetProperty("producer").GetString());
        using var replay = await producer.GetAsync(new Uri($"/internal/files/v1/publications/{publicationId}", UriKind.Relative));
        Assert.True(JsonElement.DeepEquals(publication, await replay.Content.ReadApiDataAsync()));
    }

    [PostgresFact]
    public async Task Explicit_private_intermediate_chain_allows_only_the_mapped_producer()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates(useIntermediate: true);
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        Assert.True(await StageAsync(producer, Guid.NewGuid(), owner) > 0);
    }

    [PostgresFact]
    public async Task Certificate_producer_can_seal_bytes_but_staged_file_cannot_be_downloaded_by_owner()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.Parse("9360c6b4-2a3a-499d-bd2a-4a663c078cbc");
        using var registered = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative), Description(owner));
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var receipt = await registered.Content.ReadApiDataAsync();
        var fileId = receipt.GetProperty("fileId").ReadHttpInt64();
        Assert.Equal("Pending", receipt.GetProperty("stage").GetString());
        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var sealedFile = await producer.PutAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/content", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, sealedFile.StatusCode);
        var sealedReceipt = await sealedFile.Content.ReadApiDataAsync();
        Assert.Equal(fileId, sealedReceipt.GetProperty("fileId").ReadHttpInt64());
        Assert.Equal("Staged", sealedReceipt.GetProperty("stage").GetString());
        using var metadata = await host.Client.GetAsync(new Uri($"/api/files/{fileId}/metadata", UriKind.Relative));
        using var download = await host.Client.GetAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [PostgresFact]
    public async Task Published_receipt_survives_restart_and_deleted_content_cannot_be_revived_by_replay()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var storage = new JourneyFileStorage();
        var uploadId = Guid.Parse("9360c6b4-2a3a-499d-bd2a-4a663c078cbc");
        var publicationId = Guid.Parse("1202e054-1005-4bff-9f2f-d69514c83719");
        long fileId;
        DateTimeOffset publishedAt;
        DateTimeOffset expiresAt;
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler()))
        {
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
            var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
            using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
            fileId = await StageAsync(producer, uploadId, owner);
            using var published = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
            var receipt = await published.Content.ReadApiDataAsync();
            Assert.Equal(fileId, receipt.GetProperty("fileId").ReadHttpInt64());
            Assert.Equal(publicationId, receipt.GetProperty("publicationId").GetGuid());
            publishedAt = receipt.GetProperty("publishedAt").GetDateTimeOffset();
            expiresAt = receipt.GetProperty("expiresAt").GetDateTimeOffset();
            Assert.Equal(TimeSpan.FromDays(7), expiresAt - publishedAt);
            using var download = await host.Client.GetAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
        }
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "generated-files-root-password");
        using var retryingProducer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = restarted.Client.BaseAddress };
        using var recovered = await retryingProducer.GetAsync(new Uri($"/internal/files/v1/publications/{publicationId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var original = await recovered.Content.ReadApiDataAsync();
        Assert.Equal(fileId, original.GetProperty("fileId").ReadHttpInt64());
        Assert.Equal(publishedAt, original.GetProperty("publishedAt").GetDateTimeOffset());
        Assert.Equal(expiresAt, original.GetProperty("expiresAt").GetDateTimeOffset());
        await DeleteAndWaitAsync(restarted.Client, fileId);
        using var replay = await retryingProducer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadApiDataAsync();
        Assert.Equal(expiresAt, replayed.GetProperty("expiresAt").GetDateTimeOffset());
        using var availability = await retryingProducer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative));
        Assert.Equal("Deleted", (await availability.Content.ReadApiDataAsync()).GetProperty("state").GetString());
        using var forbidden = await restarted.Client.GetAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [PostgresFact]
    public Task Expiry_blocks_download_before_cleanup_and_recovery_preserves_expired_publication_history() => VerifyExpiryAsync("csv");

    [PostgresFact]
    public Task Expired_xlsx_is_not_downloadable_and_replaying_original_publication_after_restart_does_not_extend_its_deadline() => VerifyExpiryAsync("xlsx");

    private async Task VerifyExpiryAsync(string format)
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var storage = new JourneyFileStorage();
        var settings = new Dictionary<string, string>(certificates.Settings)
        {
            ["Files__Generated__DownloadLifetimeSeconds"] = "2",
            ["Files__Cleanup__IntervalSeconds"] = "3600",
        };
        var uploadId = Guid.Parse("a97408cc-3eeb-4158-a4a3-2f0597f9420f");
        var publicationId = Guid.Parse("124b099e-68c8-4055-ab0e-79b95b943f9b");
        long fileId;
        DateTimeOffset expiresAt;
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler()))
        {
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
            var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
            using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
            fileId = await StageAsync(producer, uploadId, owner, format);
            using var published = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
            expiresAt = (await published.Content.ReadApiDataAsync()).GetProperty("expiresAt").GetDateTimeOffset();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                using var download = await host.Client.GetAsync(new Uri($"/api/files/{fileId}", UriKind.Relative), budget.Token);
                if (download.StatusCode == HttpStatusCode.NotFound) { break; }
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                await Task.Delay(50, budget.Token);
            }
            using var metadata = await host.Client.GetAsync(new Uri($"/api/files/{fileId}/metadata", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
            using var availability = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative));
            Assert.Equal("Expired", (await availability.Content.ReadApiDataAsync()).GetProperty("state").GetString());
        }
        settings["Files__Cleanup__IntervalSeconds"] = "1";
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "generated-files-root-password");
        using var retryingProducer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = restarted.Client.BaseAddress };
        using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var state = await restarted.Client.GetAsync(new Uri($"/api/files/{fileId}/deletion", UriKind.Relative), cleanupBudget.Token);
            if (state.StatusCode == HttpStatusCode.OK && (await state.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean()) { break; }
            Assert.Contains(state.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.OK });
            await Task.Delay(50, cleanupBudget.Token);
        }
        using var expired = await retryingProducer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative));
        Assert.Equal("Expired", (await expired.Content.ReadApiDataAsync()).GetProperty("state").GetString());
        using var replay = await retryingProducer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(expiresAt, (await replay.Content.ReadApiDataAsync()).GetProperty("expiresAt").GetDateTimeOffset());
        using var forbidden = await restarted.Client.GetAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [PostgresFact]
    public async Task User_token_and_forged_certificate_header_cannot_authorize_producer_or_disclose_internal_openapi()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        using var fake = new HttpRequestMessage(HttpMethod.Get, new Uri("/internal/files/v1/uploads/9360c6b4-2a3a-499d-bd2a-4a663c078cbc", UriKind.Relative));
        fake.Headers.Add("X-ARR-ClientCert", Convert.ToBase64String(certificates.Producer.RawData));
        using var forbidden = await host.Client.SendAsync(fake);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var document = await host.Client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        var openapi = await document.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(openapi.GetProperty("paths").EnumerateObject());
        Assert.DoesNotContain(openapi.GetProperty("paths").EnumerateObject(), path => path.Name.StartsWith("/internal/", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Corrupt_complete_content_is_rejected_without_sealing_then_same_upload_can_recover()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.Parse("2878cd7f-06cc-4d8a-bfbd-21910266b12c");
        using var registered = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative), Description(owner));
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var first = await registered.Content.ReadApiDataAsync();
        using var corruptBytes = new ByteArrayContent([1, 2, 4]);
        using var corrupt = await producer.PutAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/content", UriKind.Relative), corruptBytes);
        Assert.Equal(HttpStatusCode.Conflict, corrupt.StatusCode);
        var error = await corrupt.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("files.candidate.conflict", error.GetProperty("errorCode").GetString());
        using var pending = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative));
        Assert.True(JsonElement.DeepEquals(first, await pending.Content.ReadApiDataAsync()));
        var recoveredId = await StageAsync(producer, uploadId, owner);
        Assert.Equal(first.GetProperty("fileId").ReadHttpInt64(), recoveredId);
        using var repeatBytes = new ByteArrayContent([1, 2, 4]);
        using var wrongReplay = await producer.PutAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/content", UriKind.Relative), repeatBytes);
        Assert.Equal(HttpStatusCode.Conflict, wrongReplay.StatusCode);
    }

    [PostgresFact]
    public async Task Thirty_two_mebibyte_artifact_uses_declared_protocol_limit_instead_of_kestrel_default()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.Parse("2532b8a8-f6c4-4d99-a717-3660c64c2e61");
        using var registered = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative), Description(owner) with
        {
            // Golden digest of a 32 MiB all-zero fixture, fixed independently of the upload implementation.
            Sha256 = "83ee47245398adee79bd9c0a8bc57b821e92aba10f5f9ade8a5d1fae4d8c4302",
            Length = 32 * 1024 * 1024,
        });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        using var bytes = new ByteArrayContent(new byte[32 * 1024 * 1024]);
        using var sealedFile = await producer.PutAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/content", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, sealedFile.StatusCode);
        Assert.Equal("Staged", (await sealedFile.Content.ReadApiDataAsync()).GetProperty("stage").GetString());
    }

    [PostgresFact]
    public async Task Trusted_but_unmapped_certificate_is_forbidden_and_wrong_root_is_rejected_during_tls()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var wrongAuthority = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        using var unmapped = certificates.CreateUnapprovedProducer();
        using var unapproved = new HttpClient(certificates.CreateHandler(unmapped)) { BaseAddress = host.Client.BaseAddress };
        var path = new Uri("/internal/files/v1/uploads/9360c6b4-2a3a-499d-bd2a-4a663c078cbc", UriKind.Relative);
        using var forbidden = await unapproved.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var wrongRoot = new HttpClient(certificates.CreateHandler(wrongAuthority.Producer)) { BaseAddress = host.Client.BaseAddress };
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongRoot.GetAsync(path));
    }

    [PostgresFact]
    public async Task Only_current_owner_can_observe_or_download_published_artifact_and_logout_does_not_cancel_producer()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        using var registered = await host.Client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), new
        { userName = "generated-files-owner", password = "generated-files-owner-password" });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var ownerClient = new HttpClient(certificates.CreateHandler()) { BaseAddress = host.Client.BaseAddress };
        await PlatformSettingsAccessTests.LoginAsync(ownerClient, "generated-files-owner", "generated-files-owner-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(ownerClient.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.Parse("761e6f37-fbdc-46b6-9c12-a8471ba62f08");
        var fileId = await StageAsync(producer, uploadId, owner);
        using var logout = await ownerClient.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var published = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new
        { publicationId = Guid.Parse("d7a604bf-2b13-442d-9223-f6588a744a18") });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        foreach (var suffix in new[] { "", "/metadata", "/availability", "/deletion" })
        {
            using var revoked = await ownerClient.GetAsync(new Uri($"/api/files/{fileId}{suffix}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using var other = await host.Client.GetAsync(new Uri($"/api/files/{fileId}{suffix}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }
        using var forbiddenDelete = await host.Client.DeleteAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, forbiddenDelete.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(ownerClient, "generated-files-owner", "generated-files-owner-password");
        using var current = await ownerClient.GetAsync(new Uri($"/api/files/{fileId}/availability", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("Available", (await current.Content.ReadApiDataAsync()).GetProperty("state").GetString());
        using var download = await ownerClient.GetAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
        using var producerRead = await producer.GetAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, producerRead.StatusCode);
    }

    [PostgresFact]
    public async Task Producer_namespace_and_concurrent_publication_decisions_cannot_replace_or_duplicate_artifact()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var otherCredential = certificates.CreateUnapprovedProducer();
        var settings = new Dictionary<string, string>(certificates.Settings)
        {
            ["Files__Producer__Certificates__1__Sha256"] = otherCredential.GetCertHashString(HashAlgorithmName.SHA256),
            ["Files__Producer__Certificates__1__Producer"] = "costing",
        };
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        using var other = new HttpClient(certificates.CreateHandler(otherCredential)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.Parse("99a07953-9dd2-4c92-a777-a4b96773216c");
        var fileId = await StageAsync(producer, uploadId, owner);
        foreach (var suffix in new[] { "", "/availability" })
        {
            using var unknown = await other.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}{suffix}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        var ids = new[] { Guid.Parse("88fdbe06-cc42-4d20-b3b6-6cfa8d7bebad"), Guid.Parse("02d47bb6-0d03-4855-a5f8-b314fc6876f9") };
        var attempts = await Task.WhenAll(ids.Select(async publicationId =>
        {
            using var result = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
            return (Id: publicationId, Status: result.StatusCode);
        }));
        var winner = Assert.Single(attempts, item => item.Status == HttpStatusCode.OK).Id;
        Assert.Single(attempts, item => item.Status == HttpStatusCode.Conflict);
        using var hidden = await other.GetAsync(new Uri($"/internal/files/v1/publications/{winner}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var second = Guid.Parse("c1f7b9d6-0a52-45c0-b6aa-10791ec08d5a");
        var secondId = await StageAsync(producer, second, owner);
        Assert.NotEqual(fileId, secondId);
        using var duplicatePublication = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{second}/publish", UriKind.Relative), new { publicationId = winner });
        Assert.Equal(HttpStatusCode.Conflict, duplicatePublication.StatusCode);
        using var recovery = await producer.GetAsync(new Uri($"/internal/files/v1/publications/{winner}", UriKind.Relative));
        Assert.Equal(fileId, (await recovery.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64());
        using var conflictDescription = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative), Description("different-owner"));
        Assert.Equal(HttpStatusCode.Conflict, conflictDescription.StatusCode);
    }

    [PostgresFact]
    public async Task Interrupted_stream_preserves_pending_identity_and_releases_shared_upload_slot_for_retry()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.Parse("916a4b23-1a28-4b57-a09d-6d043ae146dc");
        var path = new Uri($"/internal/files/v1/uploads/{uploadId}/content", UriKind.Relative);
        using var registered = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative), Description(owner));
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var original = await registered.Content.ReadApiDataAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var slowContent = new PausedUploadContent(cancellation.Token);
        var slow = producer.PutAsync(path, slowContent, cancellation.Token);
        try
        {
            // Storage establishes entry into the stream; candidate state is observed only through HTTP.
            while (!Directory.EnumerateFiles(host.FilesRoot, "v1-*", SearchOption.AllDirectories).Any()) { await Task.Delay(20, cancellation.Token); }
            using var competingBytes = new ByteArrayContent([1, 2, 3]);
            using var competing = await producer.PutAsync(path, competingBytes);
            Assert.Equal(HttpStatusCode.TooManyRequests, competing.StatusCode);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { using var response = await slow; }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
        }
        using var pending = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative));
        Assert.True(JsonElement.DeepEquals(original, await pending.Content.ReadApiDataAsync()));
        using var retryBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var complete = new ByteArrayContent([1, 2, 3]);
            using var retried = await producer.PutAsync(path, complete, retryBudget.Token);
            if (retried.StatusCode == HttpStatusCode.Created)
            { Assert.Equal(original.GetProperty("fileId").ReadHttpInt64(), (await retried.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64()); break; }
            Assert.Equal(HttpStatusCode.TooManyRequests, retried.StatusCode);
            await Task.Delay(50, retryBudget.Token);
        }
    }

    [RabbitMqFact]
    public async Task Committed_publication_and_expiry_survive_source_restart_and_replays_add_no_audit_facts()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var storage = new JourneyFileStorage();
        var settings = new Dictionary<string, string>(certificates.Settings) { ["Files__Generated__DownloadLifetimeSeconds"] = "2" };
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-generated-files", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = StoredFileCommittedV1.Name, ConsumerName = prefix + "-files" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
        ]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            long fileId;
            var uploadId = Guid.Parse("e170f051-554d-4b76-a299-45e8f7e174dc");
            var publicationId = Guid.Parse("d783d071-83a3-40b2-8699-4335c27a23f7");
            await using (var source = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
                settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler()))
            {
                await PlatformSettingsAccessTests.LoginAsync(source.Client, "journey-root", "generated-files-root-password");
                var owner = new JwtSecurityTokenHandler().ReadJwtToken(source.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
                using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = source.Client.BaseAddress };
                fileId = await StageAsync(producer, uploadId, owner);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    using var published = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
                    Assert.Equal(HttpStatusCode.OK, published.StatusCode);
                }
                await source.CrashAsync();
            }
            foreach (var (key, value) in AuditBusinessJourneyTests.Settings(broker, prefix)) { settings[key] = value; }
            await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
                settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
            await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "generated-files-root-password");
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonElement[] facts;
            while (true)
            {
                using var read = await resumed.Client.GetAsync(new Uri($"/api/auditing/entries?source=files&subjectId={fileId}", UriKind.Relative), budget.Token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                facts = (await read.Content.ReadApiDataAsync()).EnumerateArray().Select(entry => entry.GetProperty("fact").Clone()).ToArray();
                if (facts.Any(fact => fact.GetProperty("action").GetString() == "files.stored-file.bytes-removed")) { break; }
                await Task.Delay(50, budget.Token);
            }
            Assert.Equal(6, facts.Length);
            Assert.Single(facts, fact => fact.GetProperty("action").GetString() == "files.stored-file.published" && fact.GetProperty("subjectVersion").ReadHttpInt64() == 3);
            Assert.Single(facts, fact => fact.GetProperty("action").GetString() == "files.stored-file.expired" && fact.GetProperty("subjectVersion").ReadHttpInt64() == 4);
            Assert.Single(facts, fact => fact.GetProperty("action").GetString() == "files.stored-file.deletion-requested" && fact.GetProperty("subjectVersion").ReadHttpInt64() == 4);
            Assert.All(facts, fact => Assert.DoesNotContain("export.csv", fact.GetRawText(), StringComparison.Ordinal));
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [PostgresFact]
    public async Task Deferred_fact_failure_rolls_back_publication_and_same_identity_can_retry_after_recovery()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.Parse("6d8f42f2-6eb6-4355-bd1a-beab1053ebce");
        var publicationId = Guid.Parse("9d20a9bb-2d45-4f68-bf82-d75912665fcb");
        var id = await StageAsync(producer, uploadId, owner);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        // Failure injection only, scoped to this owned journey database; business outcomes are read over HTTP.
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION files.reject_generated_publication() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Payload"::jsonb->>'operation' = 'published' THEN
                    RAISE EXCEPTION 'Injected deferred publication failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER reject_generated_publication AFTER INSERT ON files.outbox
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION files.reject_generated_publication();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        using var refused = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
        using var absent = await producer.GetAsync(new Uri($"/internal/files/v1/publications/{publicationId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var staged = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative));
        Assert.Equal("Staged", (await staged.Content.ReadApiDataAsync()).GetProperty("state").GetString());
        using var hidden = await host.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        await using (var repair = new NpgsqlCommand("DROP TRIGGER reject_generated_publication ON files.outbox; DROP FUNCTION files.reject_generated_publication();", connection))
        { await repair.ExecuteNonQueryAsync(); }
        using var retried = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(id, (await retried.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task Transient_database_retries_share_one_decision_budget_and_same_publication_can_recover()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer))
        { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(60) };
        var uploadId = Guid.NewGuid();
        var publicationId = Guid.NewGuid();
        var id = await StageAsync(producer, uploadId, owner);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION files.retry_generated_publication() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Candidate_PublicationId" IS NOT NULL THEN
                    PERFORM pg_sleep(4);
                    RAISE EXCEPTION 'Injected transient publication failure' USING ERRCODE = '40P01';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER retry_generated_publication BEFORE UPDATE ON files.stored_files
            FOR EACH ROW EXECUTE FUNCTION files.retry_generated_publication();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var refused = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(25), "All retries must share the 15-second decision deadline.");
        using var absent = await producer.GetAsync(new Uri($"/internal/files/v1/publications/{publicationId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        await using (var repair = new NpgsqlCommand("DROP TRIGGER retry_generated_publication ON files.stored_files; DROP FUNCTION files.retry_generated_publication();", connection))
        { await repair.ExecuteNonQueryAsync(); }
        using var retried = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(id, (await retried.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task Unread_publication_response_recovers_after_restart_and_storage_outage_keeps_original_receipt()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var storage = new JourneyFileStorage();
        var uploadId = Guid.NewGuid();
        var publicationId = Guid.NewGuid();
        long id;
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler()))
        {
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
            var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
            using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
            id = await StageAsync(producer, uploadId, owner);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using (var transport = new TcpClient())
            using (var handler = certificates.CreateHandler(certificates.Producer))
            {
                var address = host.Client.BaseAddress!;
                await transport.ConnectAsync(address.Host, address.Port, budget.Token);
                await using var tls = new SslStream(transport.GetStream());
                handler.SslOptions.TargetHost = address.Host;
                await tls.AuthenticateAsClientAsync(handler.SslOptions, budget.Token);
                var body = JsonSerializer.Serialize(new { publicationId });
                var request = $"POST /internal/files/v1/uploads/{uploadId}/publish HTTP/1.1\r\nHost: {address.Authority}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                await tls.WriteAsync(Encoding.UTF8.GetBytes(request), budget.Token);
                await tls.FlushAsync(budget.Token);
                // The producer never reads the response. Owner HTTP is the independent commit visibility barrier.
                while (true)
                {
                    using var observed = await host.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative), budget.Token);
                    if (observed.StatusCode == HttpStatusCode.OK)
                    {
                        Assert.Equal(new byte[] { 1, 2, 3 }, await observed.Content.ReadAsByteArrayAsync(budget.Token));
                        break;
                    }
                    Assert.Equal(HttpStatusCode.NotFound, observed.StatusCode);
                    await Task.Delay(50, budget.Token);
                }
            }
            await host.CrashAsync();
        }
        await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "generated-files-root-password");
        using var retryingProducer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = resumed.Client.BaseAddress };
        using var recovered = await retryingProducer.GetAsync(new Uri($"/internal/files/v1/publications/{publicationId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var receipt = await recovered.Content.ReadApiDataAsync();
        Assert.Equal(id, receipt.GetProperty("fileId").ReadHttpInt64());
        var originalRoot = Path.GetFullPath(storage.Root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), originalRoot, StringComparison.OrdinalIgnoreCase);
        var offline = originalRoot + "-offline";
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(offline), StringComparison.OrdinalIgnoreCase);
        Directory.Move(originalRoot, offline);
        try
        {
            await File.WriteAllTextAsync(originalRoot, "Owned storage outage barrier");
            using var unavailable = await resumed.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            using var state = await retryingProducer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative));
            Assert.Equal("StorageUnavailable", (await state.Content.ReadApiDataAsync()).GetProperty("state").GetString());
            using var history = await retryingProducer.GetAsync(new Uri($"/internal/files/v1/publications/{publicationId}", UriKind.Relative));
            Assert.True(JsonElement.DeepEquals(receipt, await history.Content.ReadApiDataAsync()));
        }
        finally
        {
            if (File.Exists(originalRoot)) { File.Delete(originalRoot); }
            Directory.Move(offline, originalRoot);
        }
        using var restored = await resumed.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await restored.Content.ReadAsByteArrayAsync());
        using var replayed = await retryingProducer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.True(JsonElement.DeepEquals(receipt, await replayed.Content.ReadApiDataAsync()));
    }

    [PostgresFact]
    public async Task Expired_unpublished_candidate_keeps_private_tombstone_and_cannot_win_publication()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        var settings = new Dictionary<string, string>(certificates.Settings) { ["Files__Generated__StageLifetimeSeconds"] = "2" };
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.NewGuid();
        var id = await StageAsync(producer, uploadId, owner);
        using var registered = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative));
        var receipt = await registered.Content.ReadApiDataAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var state = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative), budget.Token);
            var current = await state.Content.ReadApiDataAsync();
            if (current.GetProperty("state").GetString() == "Expired") { break; }
            await Task.Delay(50, budget.Token);
        }
        using var publish = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        while (true)
        {
            using var state = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative), budget.Token);
            if ((await state.Content.ReadApiDataAsync()).GetProperty("cleanupCompleted").GetBoolean()) { break; }
            await Task.Delay(50, budget.Token);
        }
        foreach (var path in new[] { $"/api/files/{id}", $"/api/files/{id}/metadata", $"/api/files/{id}/availability", $"/api/files/{id}/deletion" })
        {
            using var hidden = await host.Client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        using var deletion = await host.Client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, deletion.StatusCode);
        Assert.Equal(id, await StageAsync(producer, uploadId, owner));
        using var replayed = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative));
        Assert.True(JsonElement.DeepEquals(receipt, await replayed.Content.ReadApiDataAsync()));
        using var stillClosed = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, stillClosed.StatusCode);
    }

    [PostgresFact]
    public async Task Explicitly_mapped_invalid_credentials_cannot_bypass_certificate_validity_or_leaf_usage()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        var rejected = new[] { "expired", "server-purpose", "missing-purpose", "missing-key-usage" }
            .Select(certificates.CreateRejectedProducer).ToArray();
        try
        {
            var settings = new Dictionary<string, string>(certificates.Settings);
            var mapped = rejected.Append(certificates.Root).ToArray();
            for (var index = 0; index < mapped.Length; index++)
            {
                settings[$"Files__Producer__Certificates__{index + 1}__Sha256"] = mapped[index].GetCertHashString(HashAlgorithmName.SHA256);
                settings[$"Files__Producer__Certificates__{index + 1}__Producer"] = "pricing";
            }
            await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
                settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
            foreach (var certificate in mapped)
            {
                using var invalid = new HttpClient(certificates.CreateHandler(certificate)) { BaseAddress = host.Client.BaseAddress };
                try
                {
                    using var response = await invalid.GetAsync(new Uri("/internal/files/v1/uploads/9360c6b4-2a3a-499d-bd2a-4a663c078cbc", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                }
                catch (HttpRequestException) { /* TLS rejected the transmitted credential before HTTP. */ }
            }
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
            var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
            using var valid = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
            Assert.True(await StageAsync(valid, Guid.NewGuid(), owner) > 0);
        }
        finally { foreach (var certificate in rejected) { certificate.Dispose(); } }
    }

    [PostgresFact]
    public async Task Plain_http_forged_certificate_is_forbidden_and_production_cannot_disable_revocation()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", settings: certificates.Settings))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/internal/files/v1/uploads/9360c6b4-2a3a-499d-bd2a-4a663c078cbc", UriKind.Relative));
            request.Headers.Add("X-ARR-ClientCert", Convert.ToBase64String(certificates.Producer.RawData));
            using var forbidden = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        var production = new Dictionary<string, string>(certificates.Settings) { ["DOTNET_ENVIRONMENT"] = "Production" };
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => PlatformHostProcess.StartAsync(database.ConnectionString,
            "generated-files-root-password", settings: production, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler()));
        var diagnostic = Assert.IsType<string>(rejected.Data["DiagnosticPath"]);
        Assert.Contains("生产环境必须启用吊销检查", await File.ReadAllTextAsync(diagnostic), StringComparison.Ordinal);
        production["Files__Producer__RevocationMode"] = "Online";
        await using var secure = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: production, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        using var unknownRevocation = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = secure.Client.BaseAddress };
        // This private CA has no CRL/OCSP. An otherwise valid mapped credential must fail closed on unknown revocation.
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var response = await unknownRevocation.GetAsync(new Uri("/internal/files/v1/uploads/9360c6b4-2a3a-499d-bd2a-4a663c078cbc", UriKind.Relative));
        });
    }

    [PostgresFact]
    public async Task Process_death_during_seal_commit_recovers_the_same_upload_without_losing_committed_bytes()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var storage = new JourneyFileStorage();
        var uploadId = Guid.NewGuid();
        long id;
        await using var observer = new NpgsqlConnection(database.ConnectionString);
        await observer.OpenAsync();
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler()))
        {
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
            var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
            using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
            using var registered = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative), Description(owner));
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
            id = (await registered.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
            await using (var setup = new NpgsqlCommand("""
                CREATE FUNCTION files.pause_generated_seal_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Payload"::jsonb->>'operation' = 'stored' THEN
                        PERFORM pg_advisory_xact_lock(149149, 1);
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER pause_generated_seal_commit AFTER INSERT ON files.outbox
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION files.pause_generated_seal_commit();
                """, observer)) { await setup.ExecuteNonQueryAsync(); }
            await using var blocker = new NpgsqlConnection(database.ConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(149149, 1)", blocker, transaction)) { await hold.ExecuteNonQueryAsync(); }
            using var bytes = new ByteArrayContent([1, 2, 3]);
            var sealing = producer.PutAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/content", UriKind.Relative), bytes);
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await using var signal = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                        AND query ILIKE 'COMMIT%' AND wait_event = 'advisory')
                    """, observer);
                while (!(bool)(await signal.ExecuteScalarAsync(budget.Token))!) { await Task.Delay(10, budget.Token); }
                await host.CrashAsync();
            }
            finally { await transaction.RollbackAsync(); }
            await Assert.ThrowsAsync<HttpRequestException>(async () => { using var response = await sealing; });
        }
        await using (var repair = new NpgsqlCommand("DROP TRIGGER pause_generated_seal_commit ON files.outbox; DROP FUNCTION files.pause_generated_seal_commit();", observer))
        { await repair.ExecuteNonQueryAsync(); }
        await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "generated-files-root-password");
        var recoveredOwner = new JwtSecurityTokenHandler().ReadJwtToken(resumed.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var retrying = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = resumed.Client.BaseAddress };
        using var receipt = await retrying.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal(id, (await receipt.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64());
        Assert.Equal(id, await StageAsync(retrying, uploadId, recoveredOwner));
        using var published = await retrying.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        using var download = await resumed.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task Memory_adapter_obeys_seal_publish_owner_and_terminal_replay_contract_through_https()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        var settings = new Dictionary<string, string>(certificates.Settings) { ["Files__Storage__Provider"] = "Memory" };
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.NewGuid();
        var id = await StageAsync(producer, uploadId, owner);
        using var hidden = await host.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var publicationId = Guid.NewGuid();
        using var published = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var receipt = await published.Content.ReadApiDataAsync();
        using var download = await host.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
        using var metadata = await host.Client.GetAsync(new Uri($"/api/files/{id}/metadata", UriKind.Relative));
        var audit = (await metadata.Content.ReadApiDataAsync()).GetProperty("audit");
        Assert.Equal(JsonValueKind.Null, audit.GetProperty("createdBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, audit.GetProperty("updatedBy").ValueKind);
        Assert.NotEqual(default, audit.GetProperty("createdAt").GetDateTimeOffset());
        await DeleteAndWaitAsync(host.Client, id);
        using var replayed = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
        Assert.True(JsonElement.DeepEquals(receipt, await replayed.Content.ReadApiDataAsync()));
        using var terminal = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative));
        var state = await terminal.Content.ReadApiDataAsync();
        Assert.Equal("Deleted", state.GetProperty("state").GetString());
        Assert.True(state.GetProperty("cleanupCompleted").GetBoolean());
    }

    [PostgresFact]
    public async Task Publication_that_wins_before_stage_deadline_cannot_be_expired_by_a_stale_cleanup_scan()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        var settings = new Dictionary<string, string>(certificates.Settings)
        {
            ["Files__Generated__StageLifetimeSeconds"] = "4",
            ["Files__Cleanup__OrphanAgeSeconds"] = "3600",
        };
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
        var uploadId = Guid.NewGuid();
        var id = await StageAsync(producer, uploadId, owner);
        await using var observer = new NpgsqlConnection(database.ConnectionString);
        await observer.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            CREATE FUNCTION files.pause_generated_publish_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Payload"::jsonb->>'operation' = 'published' THEN
                    PERFORM set_config('lock_timeout', '10s', true);
                    PERFORM pg_advisory_xact_lock(149149, 2);
                END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER pause_generated_publish_commit AFTER INSERT ON files.outbox
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION files.pause_generated_publish_commit();
            """, observer)) { await setup.ExecuteNonQueryAsync(); }
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(149149, 2)", blocker, transaction)) { await hold.ExecuteNonQueryAsync(); }
        var publishing = producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId = Guid.NewGuid() });
        var cleanupPid = 0;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var signal = new NpgsqlCommand("""
                SELECT waiting.pid FROM pg_locks waiting
                JOIN pg_stat_activity publisher ON publisher.pid = ANY(pg_blocking_pids(waiting.pid))
                WHERE waiting.locktype = 'advisory' AND NOT waiting.granted
                    AND waiting.database = (SELECT oid FROM pg_database WHERE datname = current_database())
                    AND waiting.classid::bigint = ((hashtextextended(@identity, 149149) >> 32) & 4294967295)
                    AND waiting.objid::bigint = (hashtextextended(@identity, 149149) & 4294967295)
                    AND waiting.objsubid = 1
                    AND publisher.datname = current_database() AND publisher.query ILIKE 'COMMIT%'
                    AND publisher.wait_event = 'advisory' AND @barrier = ANY(pg_blocking_pids(publisher.pid))
                LIMIT 1
                """, observer);
            signal.Parameters.AddWithValue("identity", "pricing/" + uploadId.ToString("N"));
            signal.Parameters.AddWithValue("barrier", blocker.ProcessID);
            // Cleanup has selected the old stage deadline and is waiting on the publisher's transaction lock.
            while (true)
            {
                if (await signal.ExecuteScalarAsync(budget.Token) is int pid) { cleanupPid = pid; break; }
                await Task.Delay(10, budget.Token);
            }
        }
        finally
        {
            await transaction.RollbackAsync();
            if (cleanupPid == 0) { using var rejected = await publishing; }
        }
        using var published = await publishing;
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var completed = new NpgsqlCommand("""
            SELECT NOT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @cleanup AND locktype = 'advisory'
                AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND classid::bigint = ((hashtextextended(@identity, 149149) >> 32) & 4294967295)
                AND objid::bigint = (hashtextextended(@identity, 149149) & 4294967295) AND objsubid = 1)
            """, observer);
        completed.Parameters.AddWithValue("identity", "pricing/" + uploadId.ToString("N"));
        completed.Parameters.AddWithValue("cleanup", cleanupPid);
        while (!(bool)(await completed.ExecuteScalarAsync(cleanupBudget.Token))!) { await Task.Delay(10, cleanupBudget.Token); }
        using var receipt = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative));
        Assert.Equal("Available", (await receipt.Content.ReadApiDataAsync()).GetProperty("state").GetString());
        using var download = await host.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task Concurrent_registration_and_complete_seal_replays_keep_one_file_and_first_receipts()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        var settings = new Dictionary<string, string>(certificates.Settings) { ["Files__Generated__MaxConcurrentUploads"] = "2" };
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        var uploadId = Guid.NewGuid();
        var path = new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative);
        var registrations = await Task.WhenAll(producer.PostAsJsonAsync(path, Description(owner)), producer.PostAsJsonAsync(path, Description(owner)));
        using var first = registrations[0];
        using var repeated = registrations[1];
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        var original = await first.Content.ReadApiDataAsync();
        Assert.True(JsonElement.DeepEquals(original, await repeated.Content.ReadApiDataAsync()));
        using var firstBytes = new ByteArrayContent([1, 2, 3]);
        using var otherBytes = new ByteArrayContent([1, 2, 3]);
        var seals = await Task.WhenAll(producer.PutAsync(new Uri(path + "/content", UriKind.Relative), firstBytes),
            producer.PutAsync(new Uri(path + "/content", UriKind.Relative), otherBytes));
        try
        {
            Assert.All(seals, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var sealedReceipt = await seals[0].Content.ReadApiDataAsync();
            Assert.True(JsonElement.DeepEquals(sealedReceipt, await seals[1].Content.ReadApiDataAsync()));
            Assert.Equal(original.GetProperty("fileId").ReadHttpInt64(), sealedReceipt.GetProperty("fileId").ReadHttpInt64());
            using var wrongSource = await producer.PostAsJsonAsync(path, Description(owner) with { SourceExportId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.Conflict, wrongSource.StatusCode);
            using var shorter = new ByteArrayContent([1, 2]);
            using var longer = new ByteArrayContent([1, 2, 3, 4]);
            using var shortReplay = await producer.PutAsync(new Uri(path + "/content", UriKind.Relative), shorter);
            using var longReplay = await producer.PutAsync(new Uri(path + "/content", UriKind.Relative), longer);
            Assert.Equal(HttpStatusCode.Conflict, shortReplay.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, longReplay.StatusCode);
            using var recovered = await producer.GetAsync(path);
            Assert.True(JsonElement.DeepEquals(sealedReceipt, await recovered.Content.ReadApiDataAsync()));
        }
        finally { foreach (var response in seals) { response.Dispose(); } }
    }

    [PostgresFact]
    public Task Staged_candidate_prevents_schema_rollback_and_private_receipt_survives_restart() =>
        AssertCandidateRollbackRefusedAsync(deleteAfterPublication: false);

    [PostgresFact]
    public Task Deleted_candidate_prevents_schema_rollback_and_original_receipts_survive_restart() =>
        AssertCandidateRollbackRefusedAsync(deleteAfterPublication: true);

    [PostgresFact]
    public async Task Registration_committing_while_schema_rollback_waits_cannot_lose_its_upload_identity()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password",
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
        await using var observer = new NpgsqlConnection(database.ConnectionString);
        await observer.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            CREATE FUNCTION files.pause_generated_registration_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Payload"::jsonb->>'operation' = 'registered' THEN
                    PERFORM set_config('lock_timeout', '10s', true);
                    PERFORM pg_advisory_xact_lock(149149, 3);
                END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER pause_generated_registration_commit AFTER INSERT ON files.outbox
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION files.pause_generated_registration_commit();
            """, observer)) { await setup.ExecuteNonQueryAsync(); }
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(149149, 3)", blocker, transaction)) { await hold.ExecuteNonQueryAsync(); }
        await using var context = new FilesDbContext(new DbContextOptionsBuilder<FilesDbContext>()
            .UseNpgsql(database.ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "files")).Options);
        var migrator = context.GetService<IMigrator>();
        var uploadId = Guid.NewGuid();
        var path = new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative);
        var registering = producer.PostAsJsonAsync(path, Description(owner));
        Task? migrating = null;
        var released = false;
        JsonElement original = default;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await using var committing = new NpgsqlCommand("""
                SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND query ILIKE 'COMMIT%'
                    AND wait_event = 'advisory' AND @barrier = ANY(pg_blocking_pids(pid)) LIMIT 1
                """, observer);
            committing.Parameters.AddWithValue("barrier", blocker.ProcessID);
            int writerPid;
            while (true)
            {
                if (await committing.ExecuteScalarAsync(budget.Token) is int pid) { writerPid = pid; break; }
                await Task.Delay(10, budget.Token);
            }
            migrating = JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261005051650_ConditionalFactRecovery", budget.Token), budget.Token);
            await using var waiting = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'relation' AND NOT granted
                    AND relation = 'files.stored_files'::regclass
                    AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                    AND @writer = ANY(pg_blocking_pids(pid)))
                """, observer);
            waiting.Parameters.AddWithValue("writer", writerPid);
            while (!(bool)(await waiting.ExecuteScalarAsync(budget.Token))!) { await Task.Delay(10, budget.Token); }
            await transaction.RollbackAsync();
            released = true;
            using var registered = await registering;
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
            original = await registered.Content.ReadApiDataAsync();
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => migrating);
            Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
            Assert.Equal("files_candidate_history_exists", refusal.ConstraintName);
        }
        finally
        {
            try { if (!released) { await transaction.RollbackAsync(); } }
            finally
            {
                try
                {
                    // Assertions above observe the outcomes. Drain both tasks without replacing the primary failure.
                    await Task.WhenAll(registering, migrating ?? Task.CompletedTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    if (registering.IsCompletedSuccessfully) { using var completedRegistration = await registering; }
                }
                finally
                {
                    using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync(cancellationToken: cleanupBudget.Token), cleanupBudget.Token);
                }
            }
        }
        using var replayed = await producer.PostAsJsonAsync(path, Description(owner));
        Assert.Equal(HttpStatusCode.Accepted, replayed.StatusCode);
        Assert.True(JsonElement.DeepEquals(original, await replayed.Content.ReadApiDataAsync()));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    private async Task AssertCandidateRollbackRefusedAsync(bool deleteAfterPublication)
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        using var storage = new JourneyFileStorage();
        var uploadId = Guid.NewGuid();
        var publicationId = Guid.NewGuid();
        JsonElement originalUpload;
        JsonElement originalPublication = default;
        long id;
        await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler()))
        {
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "generated-files-root-password");
            var owner = new JwtSecurityTokenHandler().ReadJwtToken(host.Client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
            using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.Client.BaseAddress };
            id = await StageAsync(producer, uploadId, owner);
            using var staged = await producer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
            originalUpload = await staged.Content.ReadApiDataAsync();
            if (deleteAfterPublication)
            {
                using var published = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/publish", UriKind.Relative), new { publicationId });
                Assert.Equal(HttpStatusCode.OK, published.StatusCode);
                originalPublication = await published.Content.ReadApiDataAsync();
                await DeleteAndWaitAsync(host.Client, id);
            }
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var context = new FilesDbContext(new DbContextOptionsBuilder<FilesDbContext>()
            .UseNpgsql(database.ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "files")).Options))
        {
            var migrator = context.GetService<IMigrator>();
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => JourneyDatabaseOperation.RunAsync(
                () => migrator.MigrateAsync("20261005051650_ConditionalFactRecovery", budget.Token), budget.Token));
            Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
            Assert.Equal("files_candidate_history_exists", refusal.ConstraintName);
            Assert.Contains("20261009063803_PrivateGeneratedFileProtocol", await context.Database.GetAppliedMigrationsAsync(budget.Token));
        }
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "generated-files-root-password", filesRoot: storage.Root,
            settings: certificates.Settings, listenAddress: new Uri("https://127.0.0.1:0"), httpHandler: certificates.CreateHandler());
        await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "generated-files-root-password");
        using var recoveringProducer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = restarted.Client.BaseAddress };
        using var recovered = await recoveringProducer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.True(JsonElement.DeepEquals(originalUpload, await recovered.Content.ReadApiDataAsync()));
        using var forbidden = await restarted.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        if (deleteAfterPublication)
        {
            using var historical = await recoveringProducer.GetAsync(new Uri($"/internal/files/v1/publications/{publicationId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
            Assert.True(JsonElement.DeepEquals(originalPublication, await historical.Content.ReadApiDataAsync()));
            using var terminal = await recoveringProducer.GetAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/availability", UriKind.Relative));
            var state = await terminal.Content.ReadApiDataAsync();
            Assert.Equal("Deleted", state.GetProperty("state").GetString());
            Assert.True(state.GetProperty("cleanupCompleted").GetBoolean());
        }
    }

    private static async Task DeleteAndWaitAsync(HttpClient owner, long id)
    {
        using var deleted = await owner.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Contains(deleted.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Accepted });
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var status = await owner.GetAsync(new Uri($"/api/files/{id}/deletion", UriKind.Relative), budget.Token);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            if ((await status.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean()) { return; }
            await Task.Delay(50, budget.Token);
        }
    }

    private static GeneratedFileDescriptionV1 Description(string owner) => new(owner, Guid.Parse("ec2fe299-030a-44ab-929d-57904c2484a4"),
        "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", 3, "csv", 1, 1);

    private static async Task<long> StageAsync(HttpClient producer, Guid uploadId, string owner, string format = "csv")
    {
        byte[] payload = [1, 2, 3];
        var description = Description(owner);
        if (format == "xlsx")
        {
            using var workbook = new MemoryStream();
            await PricingXlsxV1.WriteAsync([new(new NexusStackNext.Pricing.Domain.PriceId(Guid.Parse("11111111-1111-1111-1111-111111111111")), 1, 80m, 0.2m, 1, 0, 0, null)], workbook);
            payload = workbook.ToArray();
            PricingWorkbookAssertions.FrozenPendingQuote(payload);
            description = description with { Format = "xlsx", Length = payload.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)) };
        }
        using var registered = await producer.PostAsJsonAsync(new Uri($"/internal/files/v1/uploads/{uploadId}", UriKind.Relative), description);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var id = (await registered.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        using var bytes = new ByteArrayContent(payload);
        using var sealedFile = await producer.PutAsync(new Uri($"/internal/files/v1/uploads/{uploadId}/content", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, sealedFile.StatusCode);
        return id;
    }
}
