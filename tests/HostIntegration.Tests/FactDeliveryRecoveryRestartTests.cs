using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Infrastructure.Persistence;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactDeliveryRecoveryRestartTests
{
    [PostgresFact]
    public Task Files_RecoveryReceiptSurvivesSourceProcessRestart_WithoutChangingMetadataOrPrivateBytes()
        => VerifySourceRestartAsync("files");

    [PostgresFact]
    public Task Scheduling_RecoveryReceiptSurvivesSourceProcessRestart_WithoutChangingNonemptyPlanHistory()
        => VerifySourceRestartAsync("scheduling");

    private static async Task VerifySourceRestartAsync(string source)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var storageRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nsn-fact-recovery-restart-" + Guid.NewGuid().ToString("N")));
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(storageRoot), expectedParent, comparison))
        { throw new InvalidOperationException("Test storage escaped its owned temporary directory."); }
        try
        {
            var requestId = Guid.NewGuid();
            DateTimeOffset stoppedAt;
            Guid messageId;
            JsonElement receipt;
            var observations = new Dictionary<Uri, JsonElement>();
            Uri? downloadPath = null;
            await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "source-recovery-restart-root", storageRoot))
            {
                await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "source-recovery-restart-root");
                if (source == "files")
                {
                    using var bytes = new ByteArrayContent([8, 4, 2]);
                    using var uploaded = await first.Client.PostAsync(new Uri("/api/files?name=restart-private.bin", UriKind.Relative), bytes);
                    Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
                    var fileId = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
                    var metadata = new Uri($"/api/files/{fileId}/metadata", UriKind.Relative);
                    observations.Add(metadata, (await first.Client.GetFromJsonAsync<JsonElement>(metadata)).GetProperty("data").Clone());
                    downloadPath = new Uri($"/api/files/{fileId}", UriKind.Relative);
                    Assert.Equal(new byte[] { 8, 4, 2 }, await first.Client.GetByteArrayAsync(downloadPath));
                }
                else
                {
                    using var created = await first.Client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
                    {
                        code = "restart-fact-recovery-plan",
                        intervalSeconds = 3600,
                        firstRunInSeconds = 0,
                        targetKind = "costing.recalculate",
                        targetId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    });
                    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                    var planId = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
                    var decisionsPath = new Uri($"/api/scheduling/tasks/{planId}/decisions", UriKind.Relative);
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    while (true)
                    {
                        var data = (await first.Client.GetFromJsonAsync<JsonElement>(decisionsPath, budget.Token)).GetProperty("data");
                        if (data.GetArrayLength() > 0) { break; }
                        await Task.Delay(50, budget.Token);
                    }
                    observations.Add(decisionsPath, (await first.Client.GetFromJsonAsync<JsonElement>(decisionsPath)).GetProperty("data").Clone());
                    Assert.Single(observations[decisionsPath].EnumerateArray());
                    var plansPath = new Uri("/api/scheduling/tasks/", UriKind.Relative);
                    observations.Add(plansPath, (await first.Client.GetFromJsonAsync<JsonElement>(plansPath)).GetProperty("data").Clone());
                    var occurrencesPath = new Uri($"/api/scheduling/tasks/{planId}/occurrences", UriKind.Relative);
                    observations.Add(occurrencesPath, (await first.Client.GetFromJsonAsync<JsonElement>(occurrencesPath)).GetProperty("data").Clone());
                    Assert.Single(observations[occurrencesPath].EnumerateArray());
                }
                await using var owned = OpenOwnedSource(source, database.ConnectionString);
                var outbox = new EfOutboxStore<NexusStackDbContext>(owned);
                var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
                Assert.Equal(source == "files" ? 2 : 3, originals.Count);
                var original = originals.Last(entry => entry.EventName == (source == "files" ? StoredFileCommittedV1.Name : PlanCommittedV1.Name));
                messageId = original.Id;
                stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
                var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
                Assert.Equal(originals.Count, (await publisher.PublishPendingAsync()).DeadLettered);
                if (source == "scheduling")
                {
                    var occurrencesPath = Assert.Single(observations.Keys, path => path.OriginalString.EndsWith("/occurrences", StringComparison.Ordinal));
                    observations[occurrencesPath] = (await first.Client.GetFromJsonAsync<JsonElement>(occurrencesPath)).GetProperty("data").Clone();
                    Assert.Equal("DeadLettered", Assert.Single(observations[occurrencesPath].EnumerateArray()).GetProperty("deliveryState").GetString());
                }
                using var accepted = await first.Client.PostAsJsonAsync(new Uri($"/api/{source}/audit-deliveries/{messageId}/retry", UriKind.Relative), new
                { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "dependency-restored" });
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                receipt = (await accepted.Content.ReadApiDataAsync()).Clone();
                Assert.Equal(source, receipt.GetProperty("source").GetString());
                var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
                Assert.Equal(original.Id, pending.Id);
                Assert.Equal(original.Payload, pending.Payload);
                Assert.Equal(original.OccurredAt, pending.OccurredAt);
                Assert.Equal(1, pending.RetryRevision);
                observations.Add(new Uri($"/api/{source}/audit-deliveries/{messageId}", UriKind.Relative),
                    (await first.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/{source}/audit-deliveries/{messageId}", UriKind.Relative))).GetProperty("data").Clone());
            }
            await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "source-recovery-restart-root", storageRoot);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "source-recovery-restart-root");
            using var found = await restarted.Client.GetAsync(new Uri($"/api/{source}/audit-deliveries/recoveries/{requestId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            Assert.True(JsonElement.DeepEquals(receipt, await found.Content.ReadApiDataAsync()));
            await using var after = OpenOwnedSource(source, database.ConnectionString);
            var restoredOutbox = new EfOutboxStore<NexusStackDbContext>(after);
            var beforeReplay = Assert.Single(await restoredOutbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
            using var replay = await restarted.Client.PostAsJsonAsync(new Uri($"/api/{source}/audit-deliveries/{messageId}/retry", UriKind.Relative), new
            { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "dependency-restored" });
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(JsonElement.DeepEquals(receipt, await replay.Content.ReadApiDataAsync()));
            Assert.Equal(beforeReplay, Assert.Single(await restoredOutbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)));
            foreach (var (path, original) in observations)
            { Assert.True(JsonElement.DeepEquals(original, (await restarted.Client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data")), path.OriginalString); }
            if (downloadPath is not null) { Assert.Equal(new byte[] { 8, 4, 2 }, await restarted.Client.GetByteArrayAsync(downloadPath)); }
        }
        finally
        {
            if (Directory.Exists(storageRoot)) { Directory.Delete(storageRoot, recursive: true); }
        }
    }

    private static NexusStackDbContext OpenOwnedSource(string source, string connection) => source switch
    {
        "files" => new FilesDbContext(new DbContextOptionsBuilder<FilesDbContext>().UseNexusStackPostgres(connection, FilesDbContext.SchemaName).Options),
        "scheduling" => new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseNexusStackPostgres(connection, SchedulingDbContext.SchemaName).Options),
        _ => throw new ArgumentException("Unknown recovery source.", nameof(source)),
    };
}
