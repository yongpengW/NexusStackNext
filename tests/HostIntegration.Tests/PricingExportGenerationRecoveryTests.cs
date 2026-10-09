using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.Files.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingExportGenerationRecoveryTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Generation_process_death_releases_temp_bytes_and_restart_reuses_pending_upload_while_stale_upload_cannot_select_publication()
    {
        await using var platform = await databases.CreateAsync();
        await using var pricing = await databases.CreateAsync("pricing");
        using var certificates = new GeneratedFileCertificates();
        var platformSettings = new Dictionary<string, string>(certificates.Settings)
        {
            ["Jwt__SigningKey"] = BusinessProcess.SigningKey,
            ["Kestrel__Endpoints__Public__Url"] = "http://127.0.0.1:0",
            ["Kestrel__Endpoints__Producer__Url"] = "https://127.0.0.1:0",
        };
        await using var host = await PlatformHostProcess.StartAsync(platform.ConnectionString, "export-test-password", settings: platformSettings);
        Assert.NotNull(host.HttpsAddress);
        await UserLifecycleHttpTests.LoginAsync(host.Client, "journey-root", "export-test-password");
        await using var fault = await GeneratedFileReplyFault.StartAsync(certificates, host.HttpsAddress, GeneratedFileFaultPoint.BeforeContent);
        var client = certificates.PricingClientOptions(fault.BaseAddress);
        using var temporary = new JourneyFileStorage();
        Directory.CreateDirectory(temporary.Root);
        var settings = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Testing",
            ["IdentitySession__BaseAddress"] = host.Client.BaseAddress!.AbsoluteUri,
            ["Pricing__Exports__Enabled"] = "true",
            ["Pricing__Exports__Execution__PollInterval"] = "00:00:00.100",
            ["Pricing__Exports__Execution__LeaseDuration"] = "00:00:03",
            ["Pricing__Exports__Files__BaseAddress"] = client.BaseAddress,
            ["Pricing__Exports__Files__ClientCertificatePath"] = client.ClientCertificatePath,
            ["Pricing__Exports__Files__ClientKeyPath"] = client.ClientKeyPath,
            ["Pricing__Exports__Files__RootCertificatePaths__0"] = client.RootCertificatePaths[0],
            ["Pricing__Exports__Files__RevocationMode"] = "NoCheck",
            ["TMP"] = temporary.Root,
            ["TEMP"] = temporary.Root,
            ["TMPDIR"] = temporary.Root,
        };
        await using var first = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricing.ConnectionString, settings: settings);
        first.Client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
        using (var cost = await first.Client.PostAsJsonAsync(Relative("/api/pricing/cost"), new { requestId = Guid.NewGuid(), itemId = item, expectedVersion = "0", cost = 80m, feeRate = 0.2m }))
        { Assert.Equal(HttpStatusCode.Accepted, cost.StatusCode); }
        using var accepted = await first.Client.PostAsJsonAsync(Relative("/api/pricing/exports"), new { requestId = Guid.NewGuid(), itemIds = new[] { item } });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var original = await accepted.Content.ReadApiDataAsync();
        var id = original.GetProperty("exportId").GetGuid();
        await fault.WaitForFaultAsync();
        using var current = await first.Client.GetAsync(Relative($"/api/pricing/exports/{id}"));
        var interrupted = await current.Content.ReadApiDataAsync();
        Assert.Equal("Generating", interrupted.GetProperty("state").GetString());
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = host.HttpsAddress, Timeout = TimeSpan.FromSeconds(10) };
        using var pending = await producer.GetAsync(Relative($"/internal/files/v1/uploads/{fault.ObservedUploadId}"));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var pendingId = (await pending.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        using (var hidden = await host.Client.GetAsync(Relative($"/api/files/{pendingId}"))) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        var address = first.Client.BaseAddress!;
        await first.CrashAsync();
        Assert.Empty(Directory.EnumerateFiles(temporary.Root, "*.csv.tmp", SearchOption.AllDirectories));
        fault.Release();
        settings["Kestrel__Endpoints__Public__Url"] = address.AbsoluteUri;
        await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricing.ConnectionString, settings: settings);
        restarted.Client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        JsonElement completed;
        while (true)
        {
            using var response = await restarted.Client.GetAsync(Relative($"/api/pricing/exports/{id}"), budget.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            completed = await response.Content.ReadApiDataAsync();
            if (completed.GetProperty("state").GetString() == "Succeeded") { break; }
            Assert.Contains(completed.GetProperty("state").GetString(), new[] { "Generating", "Publishing" });
            await Task.Delay(100, budget.Token);
        }
        Assert.Equal(pendingId, completed.GetProperty("fileId").ReadHttpInt64());
        Assert.Equal(original.GetProperty("snapshotDigest").GetString(), completed.GetProperty("snapshotDigest").GetString());
        Assert.True(completed.GetProperty("epoch").ReadHttpInt64() > interrupted.GetProperty("epoch").ReadHttpInt64());
        const string csv = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n"
            + "11111111-1111-1111-1111-111111111111,1,80.0000,0.2000,1,0,0,,Pending\r\n";
        using var lateBytes = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        using var lateUpload = await producer.PutAsync(Relative($"/internal/files/v1/uploads/{fault.ObservedUploadId}/content"), lateBytes);
        Assert.Equal(HttpStatusCode.Created, lateUpload.StatusCode);
        var receipt = (await lateUpload.Content.ReadFromJsonAsync<GeneratedReceiptEnvelope>())!.Data;
        var services = new ServiceCollection().AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        services.AddPricingPostgres(pricing.ConnectionString).AddPricingExportPersistence();
        await using var oldWorker = services.BuildServiceProvider();
        await using var scope = oldWorker.CreateAsyncScope();
        var rejected = await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new SelectPricingExportPublication(id,
            interrupted.GetProperty("epoch").ReadHttpInt64(), Guid.NewGuid(), receipt));
        Assert.Equal("pricing.export.selection_conflict", rejected.Error.Code);
        using var unchanged = await restarted.Client.GetAsync(Relative($"/api/pricing/exports/{id}"));
        Assert.True(JsonElement.DeepEquals(completed, await unchanged.Content.ReadApiDataAsync()));
        using var downloaded = await host.Client.GetAsync(Relative($"/api/files/{pendingId}"));
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(csv), await downloaded.Content.ReadAsByteArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(temporary.Root, "*.csv.tmp", SearchOption.AllDirectories));
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
    private sealed record GeneratedReceiptEnvelope(GeneratedFileReceiptV1 Data);
}
