using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class AuditExportRecoveryTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public Task Frozen_mq_fact_reaches_owner_xlsx_after_lost_publication_reply_and_process_restart() => VerifyAsync(GeneratedFileFaultPoint.AfterPublication);

    [AuditBrokerFact]
    public Task Original_operation_workbook_is_recovered_after_process_death_without_rendering_again() => VerifyAsync(GeneratedFileFaultPoint.AfterContent);

    [AuditBrokerFact]
    public Task Owner_recovers_exhausted_generation_after_death_before_content_with_original_candidate_and_bytes() => VerifyAsync(GeneratedFileFaultPoint.BeforeContent);

    private async Task VerifyAsync(GeneratedFileFaultPoint point)
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-audit", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName, [new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix }]);
        var fileSettings = new Dictionary<string, string>(certificates.Settings)
        {
            ["Files__Producer__Certificates__0__Producer"] = "auditing",
            ["Kestrel__Endpoints__Public__Url"] = "http://127.0.0.1:0",
            ["Kestrel__Endpoints__Producer__Url"] = "https://127.0.0.1:0",
            ["Auditing__Messaging__Enabled"] = "false",
        };
        await using var files = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-export-password", settings: fileSettings);
        Assert.NotNull(files.HttpsAddress);
        await using var fault = await GeneratedFileReplyFault.StartAsync(certificates, files.HttpsAddress, point);
        var transport = certificates.PricingClientOptions(fault.BaseAddress);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nsn-audit-export-recovery-" + Guid.NewGuid().ToString("N"));
        var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
        settings["DOTNET_ENVIRONMENT"] = "Testing";
        settings["Auditing__Exports__Enabled"] = "true";
        settings["Auditing__Exports__Worker__Enabled"] = "true";
        settings["Auditing__Exports__Execution__LeaseDuration"] = "00:00:03";
        settings["Auditing__Exports__Execution__PollInterval"] = "00:00:00.100";
        settings["Auditing__Exports__Execution__TemporaryDirectory"] = temporaryDirectory;
        if (point == GeneratedFileFaultPoint.BeforeContent)
        {
            settings["Auditing__Exports__Execution__MaxAttempts"] = "1";
            settings["Auditing__Exports__Execution__LeaseDuration"] = "00:00:10";
        }
        settings["Auditing__Exports__Files__BaseAddress"] = transport.BaseAddress;
        settings["Auditing__Exports__Files__ClientCertificatePath"] = transport.ClientCertificatePath;
        settings["Auditing__Exports__Files__ClientKeyPath"] = transport.ClientKeyPath;
        settings["Auditing__Exports__Files__RootCertificatePaths__0"] = transport.RootCertificatePaths[0];
        settings["Auditing__Exports__Files__RevocationMode"] = "NoCheck";
        try
        {
            await using var platform = await PlatformHostProcess.StartAsync(database.ConnectionString, filesRoot: files.FilesRoot, settings: settings);
            await using var gateway = new GatewayHttpApp(platform.Client.BaseAddress!.AbsoluteUri)
            {
                SigningKey = PersistentIdentityApp.SigningKey,
                SessionAuthorityAddress = files.Client.BaseAddress!.AbsoluteUri,
                RateLimitPermitLimit = 1000,
            };
            var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.json"))).Value;
            gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend", RateLimitPolicy = null }));
            using var owner = gateway.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(owner, "journey-root", "audit-export-password");
            using (var changed = await owner.PutAsJsonAsync(Relative("/api/platform/settings/export.precision"), new { value = "private-value-must-not-be-exported" }))
            { Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode); }
            var factPage = await AuditBusinessJourneyTests.WaitForCountAsync(owner, 1);
            var operationId = factPage.GetProperty("data").EnumerateArray().Single().GetProperty("fact").GetProperty("execution").GetProperty("operationId").GetGuid();
            if (point == GeneratedFileFaultPoint.AfterContent)
            {
                using var observationBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    using var observed = await owner.GetAsync(Relative($"/api/auditing/operations?source=platform&operationId={operationId}&outcome=completed"), observationBudget.Token);
                    Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
                    if ((await observed.Content.ReadApiDataAsync()).EnumerateArray().Any()) { break; }
                    await Task.Delay(100, observationBudget.Token);
                }
            }
            var requestId = Guid.NewGuid();
            object request = point == GeneratedFileFaultPoint.AfterContent
                ? new { requestId, operations = new { source = "platform", operationId, outcome = "completed" } }
                : new { requestId, facts = new { source = "platform", subjectId = "export.precision" } };
            using var accepted = await owner.PostAsJsonAsync(Relative("/api/auditing/exports"), request);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var original = await accepted.Content.ReadApiDataAsync();
            var id = original.GetProperty("exportId").GetGuid();
            try { await fault.WaitForFaultAsync(); }
            catch (TimeoutException)
            {
                using var stalled = await owner.GetAsync(Relative($"/api/auditing/exports/{id}"));
                var state = await stalled.Content.ReadApiDataAsync();
                Assert.Fail($"Audit export did not reach HTTPS fault; state={state.GetProperty("state").GetString()}, relay={fault.LastTransportResult}.");
            }
            using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = files.HttpsAddress };
            using var receiptReply = await producer.GetAsync(Relative($"/internal/files/v1/uploads/{id}"));
            Assert.Equal(HttpStatusCode.OK, receiptReply.StatusCode);
            var sealedFile = await receiptReply.Content.ReadApiDataAsync();
            var fileId = sealedFile.GetProperty("fileId").GetString();
            var digest = sealedFile.GetProperty("description").GetProperty("sha256").GetString();
            await platform.CrashAsync();
            fault.Release();
            Assert.Empty(Directory.EnumerateFiles(temporaryDirectory));
            if (point == GeneratedFileFaultPoint.AfterContent)
            {
                Directory.Delete(temporaryDirectory);
                await File.WriteAllTextAsync(temporaryDirectory, "rendering unavailable after sealed bytes");
            }
            settings["Kestrel__Endpoints__Public__Url"] = platform.Client.BaseAddress.AbsoluteUri;
            await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, filesRoot: files.FilesRoot, settings: settings);
            await BusinessProcess.WaitForIdentityForwardingAsync(owner, HttpStatusCode.OK);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            if (point == GeneratedFileFaultPoint.BeforeContent)
            {
                while (true)
                {
                    using var stoppedReply = await owner.GetAsync(Relative($"/api/auditing/exports/{id}"), timeout.Token);
                    var stopped = await stoppedReply.Content.ReadApiDataAsync();
                    if (stopped.GetProperty("state").GetString() == "Failed")
                    {
                        using var resumed = await owner.PostAsJsonAsync(Relative($"/api/auditing/exports/{id}/retry"),
                            new { expectedVersion = stopped.GetProperty("version").GetString() }, timeout.Token);
                        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
                        break;
                    }
                    await Task.Delay(100, timeout.Token);
                }
            }
            JsonElement completed;
            while (true)
            {
                using var statusReply = await owner.GetAsync(Relative($"/api/auditing/exports/{id}"), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, statusReply.StatusCode);
                completed = await statusReply.Content.ReadApiDataAsync();
                Assert.True(completed.GetProperty("state").GetString() != "Failed",
                    "Original retry failed: " + completed.GetProperty("errorCode").GetString() + "; relay=" + fault.LastTransportResult);
                if (completed.GetProperty("state").GetString() == "Succeeded") { break; }
                await Task.Delay(100, timeout.Token);
            }
            Assert.Equal(fileId, completed.GetProperty("fileId").GetString());
            Assert.Equal(digest, completed.GetProperty("artifactDigest").GetString());
            using var artifactReply = await owner.GetAsync(Relative($"/api/auditing/exports/{id}/artifact"));
            Assert.Equal(HttpStatusCode.OK, artifactReply.StatusCode);
            var artifact = await artifactReply.Content.ReadApiDataAsync();
            using var download = await owner.GetAsync(Relative(artifact.GetProperty("downloadPath").GetString()!));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            await using var content = await download.Content.ReadAsStreamAsync();
            using var archive = new ZipArchive(content);
            var worksheet = XDocument.Load(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var data = worksheet.Descendants(spreadsheet + "row").Skip(1).Single();
            Assert.All(data.Elements(spreadsheet + "c"), cell => { Assert.Equal("inlineStr", cell.Attribute("t")!.Value); Assert.Null(cell.Element(spreadsheet + "f")); });
            Assert.Contains(point == GeneratedFileFaultPoint.AfterContent ? "operation-observation" : "committed-fact", data.Value, StringComparison.Ordinal);
            Assert.Contains(point == GeneratedFileFaultPoint.AfterContent ? operationId.ToString("D") : "export.precision", data.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("private-value-must-not-be-exported", worksheet.ToString(), StringComparison.Ordinal);
            using var replay = await owner.PostAsJsonAsync(Relative("/api/auditing/exports"), request);
            Assert.Equal(fileId, (await replay.Content.ReadApiDataAsync()).GetProperty("fileId").GetString());
        }
        finally
        {
            if (File.Exists(temporaryDirectory)) { File.Delete(temporaryDirectory); }
            else if (Directory.Exists(temporaryDirectory)) { Directory.Delete(temporaryDirectory); }
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
        }
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
}
