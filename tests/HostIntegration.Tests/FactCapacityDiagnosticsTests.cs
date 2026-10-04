using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityDiagnosticsTests
{
    [PostgresFact]
    public async Task PostgreSqlReader_ExcludesUncommittedChanges_EvenInTheWritersScope()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new DiagnosticPersistentApp(database.ConnectionString);
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Outbox.Add(new OutboxEntry { Id = Guid.NewGuid(), EventName = SettingCommittedV1.Name, Payload = "中", OccurredAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
            var pending = await reader.ReadAsync();
            Assert.True(pending.IsSuccess);
            Assert.Equal(0, pending.Value.RetainedRecords);
            Assert.Equal(0, pending.Value.RetainedPayloadBytes);
            await transaction.CommitAsync();
        });
        var committed = await reader.ReadAsync();
        Assert.True(committed.IsSuccess);
        Assert.Equal(1, committed.Value.RetainedRecords);
        Assert.Equal(3, committed.Value.RetainedPayloadBytes);
    }

    [PostgresFact]
    public async Task SchedulingPostgres_ReadsOnlyItsCommittedLedger()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new DiagnosticPersistentApp(database.ConnectionString);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "diagnostics-root-password");
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks", UriKind.Relative), new
        {
            code = "diagnostics-pg-plan",
            intervalSeconds = 3600,
            firstRunInSeconds = 3600,
            targetKind = "costing.recalculate",
            targetId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var snapshot = await ReadAsync(client, "scheduling");
        Assert.Equal("scheduling", snapshot.GetProperty("context").GetString());
        Assert.True(snapshot.GetProperty("isPersistent").GetBoolean());
        Assert.Equal(1, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(3, snapshot.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(99999, snapshot.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.Equal(268435453, snapshot.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        Assert.Equal(0, (await ReadAsync(client, "files")).GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(snapshot.GetRawText(), (await ReadAsync(client, "scheduling")).GetRawText());
    }

    [Fact]
    public async Task SchedulingMemory_ReportsDefinitionAndPauseFacts_WithoutChangingThePlan()
    {
        await using var app = new DiagnosticApp(new MutableClock(DateTimeOffset.UtcNow), "Scheduling", 10, 30)
        { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks", UriKind.Relative), new
        {
            code = "diagnostics-plan",
            intervalSeconds = 3600,
            firstRunInSeconds = 3600,
            targetKind = "costing.recalculate",
            targetId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var planId = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        var first = await ReadAsync(client, "scheduling");
        Assert.Equal("scheduling", first.GetProperty("context").GetString());
        Assert.False(first.GetProperty("isPersistent").GetBoolean());
        Assert.Equal(1, first.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(3, first.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        using var pause = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{planId}/pause", UriKind.Relative), new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NoContent, pause.StatusCode);
        var after = await ReadAsync(client, "scheduling");
        Assert.Equal(2, after.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(6, after.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(8, after.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.Equal(24, after.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        Assert.Equal(after.GetRawText(), (await ReadAsync(client, "scheduling")).GetRawText());
        using var list = await client.GetAsync(new Uri("/api/scheduling/tasks", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var plan = Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
        Assert.Equal(2, plan.GetProperty("version").ReadHttpInt64());
        Assert.False(plan.GetProperty("isEnabled").GetBoolean());
    }

    [PostgresFact]
    public async Task FilesPostgres_ReadsCommittedUpload_WithoutCountingOtherContexts()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new DiagnosticPersistentApp(database.ConnectionString);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "diagnostics-root-password");
        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var uploaded = await client.PostAsync(new Uri("/api/files?name=capacity-pg.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var snapshot = await ReadAsync(client, "files");
        Assert.Equal("files", snapshot.GetProperty("context").GetString());
        Assert.True(snapshot.GetProperty("isPersistent").GetBoolean());
        Assert.Equal(2, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(6, snapshot.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(99998, snapshot.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.Equal(268435450, snapshot.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        Assert.Equal(3, (await ReadAsync(client, "identity")).GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(snapshot.GetRawText(), (await ReadAsync(client, "files")).GetRawText());
    }

    [Fact]
    public async Task FilesMemory_ReadsWholeUploadBatch_AndCountsDeadLettersUntilConfirmedCleanup()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var app = new DiagnosticApp(clock, "Files", 10, 30) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var uploaded = await client.PostAsync(new Uri("/api/files?name=capacity-private.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var snapshot = await ReadAsync(client, "files");
        Assert.Equal("files", snapshot.GetProperty("context").GetString());
        Assert.False(snapshot.GetProperty("isPersistent").GetBoolean());
        Assert.Equal(2, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(6, snapshot.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(8, snapshot.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.Equal(24, snapshot.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        Assert.DoesNotContain("capacity-private", snapshot.GetRawText(), StringComparison.Ordinal);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        foreach (var entry in await outbox.ReadPendingAsync(10, clock.UtcNow))
        {
            Assert.True(await outbox.MarkDeadLetteredAsync(entry.Id, "private-error", clock.UtcNow, entry.RetryRevision));
        }
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("files").CleanupAsync());
        Assert.Equal(snapshot.GetRawText(), (await ReadAsync(client, "files")).GetRawText());
    }

    [PostgresFact]
    public async Task IdentityPostgres_ReadsItsCommittedFacts_WithoutTouchingPlatformQuota()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new DiagnosticPersistentApp(database.ConnectionString);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "diagnostics-root-password");
        var snapshot = await ReadAsync(client, "identity");
        Assert.True(snapshot.GetProperty("isPersistent").GetBoolean());
        Assert.Equal("identity", snapshot.GetProperty("context").GetString());
        Assert.Equal(3, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(9, snapshot.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(99997, snapshot.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.Equal(268435447, snapshot.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        Assert.Equal(0, (await ReadAsync(client, "platform")).GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(snapshot.GetRawText(), (await ReadAsync(client, "identity")).GetRawText());
    }

    [Theory]
    [InlineData("Platform", "00:00:00.049")]
    [InlineData("Identity", "00:00:30.001")]
    [InlineData("Files", "00:00:00")]
    [InlineData("Scheduling", "-00:00:01")]
    public async Task InvalidReadBudget_RejectsMemoryComposition(string context, string timeout)
    {
        await using var app = new DiagnosticApp(new MutableClock(DateTimeOffset.UtcNow), context, 100, 1000, timeout)
        { SchedulingWorkerEnabled = false };
        var failure = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("事实容量诊断预算超出允许范围", failure.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task PlatformPostgres_ReadsOwnedLedger_ExcludesOrdinaryMessages_AndKeepsInt64Exact()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "diagnostics-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "diagnostics-root-password");
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await context.Database.ExecuteSqlRawAsync("UPDATE platform.fact_capacity SET \"MaxRecords\" = 9007199254740993, \"MaxPayloadBytes\" = 12, \"MaxRecordPayloadBytes\" = 3");
        var first = new OutboxEntry { Id = Guid.NewGuid(), EventName = SettingCommittedV1.Name, Payload = "中", OccurredAt = DateTimeOffset.UtcNow };
        context.Outbox.AddRange(first, first with { Id = Guid.NewGuid(), Payload = "文" },
            first with { Id = Guid.NewGuid(), EventName = "business.test.v1", Payload = new string('x', 1000) });
        await context.SaveChangesAsync();
        var snapshot = await ReadAsync(client, "platform");
        Assert.True(snapshot.GetProperty("isPersistent").GetBoolean());
        Assert.Equal("platform", snapshot.GetProperty("context").GetString());
        Assert.Equal("9007199254740993", snapshot.GetProperty("maxRecords").GetString());
        Assert.Equal("9007199254740991", snapshot.GetProperty("remainingRecords").GetString());
        Assert.Equal(2, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(6, snapshot.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(6, snapshot.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        Assert.False(snapshot.GetProperty("overLimit").GetBoolean());
        Assert.Equal(snapshot.GetRawText(), (await ReadAsync(client, "platform")).GetRawText());
        await context.Database.ExecuteSqlRawAsync("UPDATE platform.fact_capacity SET \"MaxRecords\" = 1, \"MaxPayloadBytes\" = 3");
        var overLimit = await ReadAsync(client, "platform");
        Assert.True(overLimit.GetProperty("overLimit").GetBoolean());
        Assert.Equal(0, overLimit.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.Equal(0, overLimit.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        Assert.Equal(2, overLimit.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(6, overLimit.GetProperty("retainedPayloadBytes").ReadHttpInt64());
    }

    [Fact]
    public async Task IdentityMemory_HasItsOwnCommittedSnapshot_AndReadingNeverCreatesFacts()
    {
        await using var app = new DiagnosticApp(new MutableClock(DateTimeOffset.UtcNow), "Identity", 10, 30)
        { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var before = await ReadAsync(client, "identity");
        Assert.Equal("identity", before.GetProperty("context").GetString());
        Assert.False(before.GetProperty("isPersistent").GetBoolean());
        // 创建根主体、成功登录与发出刷新凭据各有一条已提交事实。
        Assert.Equal(3, before.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(9, before.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        using var created = await client.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative), new { code = "capacity-reader", name = "Capacity reader" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var after = await ReadAsync(client, "identity");
        Assert.Equal(4, after.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(12, after.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(6, after.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.Equal(18, after.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        using var unrelated = await client.PutAsJsonAsync(new Uri("/api/platform/settings/diagnostic.other", UriKind.Relative), new { value = "other-context" });
        Assert.Equal(HttpStatusCode.NoContent, unrelated.StatusCode);
        Assert.Equal(after.GetRawText(), (await ReadAsync(client, "identity")).GetRawText());
        Assert.Equal(1, (await ReadAsync(client, "platform")).GetProperty("retainedRecords").ReadHttpInt64());
    }

    [Fact]
    public async Task PlatformMemory_ReadsCommittedOccupancy_AndOnlyCleanupReleasesConfirmedBytes()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var app = new DiagnosticApp(clock) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var empty = await ReadAsync(client, "platform");
        Assert.Equal("platform", empty.GetProperty("context").GetString());
        Assert.False(empty.GetProperty("isPersistent").GetBoolean());
        Assert.Equal(2, empty.GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(6, empty.GetProperty("maxPayloadBytes").ReadHttpInt64());
        Assert.Equal(3, empty.GetProperty("maxRecordPayloadBytes").GetInt32());
        Assert.Equal(0, empty.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(0, empty.GetProperty("retainedPayloadBytes").ReadHttpInt64());

        using var created = await client.PutAsJsonAsync(new Uri("/api/platform/settings/diagnostic.first", UriKind.Relative), new { value = "private-value" });
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        var retained = await ReadAsync(client, "platform");
        Assert.Equal(1, retained.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(3, retained.GetProperty("retainedPayloadBytes").ReadHttpInt64());
        Assert.Equal(1, retained.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.Equal(3, retained.GetProperty("remainingPayloadBytes").ReadHttpInt64());
        Assert.False(retained.GetProperty("overLimit").GetBoolean());
        Assert.DoesNotContain("private-value", retained.GetRawText(), StringComparison.Ordinal);

        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("platform");
        var fact = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        await outbox.MarkDeliveredAsync(fact.Id, clock.UtcNow);
        Assert.Equal(0, await cleanup.CleanupAsync());
        Assert.Equal(retained.GetRawText(), (await ReadAsync(client, "platform")).GetRawText());
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(1, await cleanup.CleanupAsync());
        Assert.Equal(empty.GetRawText(), (await ReadAsync(client, "platform")).GetRawText());
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string context)
    {
        using var response = await client.GetAsync(new Uri($"/api/{context}/audit-capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private sealed class DiagnosticPersistentApp(string connectionString)
        : PersistentIdentityApp(connectionString, "diagnostics-root-password", schedulingWorkerEnabled: false)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IIntegrationEventSerializer>(
                new KnownPayloadSerializer(new SystemTextJsonIntegrationEventSerializer())
                { UseKnownPayload = value => value is IdentityEntityCommittedV1 or StoredFileCommittedV1 or PlanCommittedV1 }));
        }
    }

    private sealed class DiagnosticApp(MutableClock clock, string context = "Platform", int maxRecords = 2, int maxBytes = 6,
        string readTimeout = "00:00:03") : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{context}:AuditDelivery:MemoryCapacity:MaxRecords"] = maxRecords.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [$"{context}:AuditDelivery:MemoryCapacity:MaxPayloadBytes"] = maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [$"{context}:AuditDelivery:MemoryCapacity:MaxRecordPayloadBytes"] = "3",
                [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
                [$"{context}:AuditDelivery:CapacityRead:Timeout"] = readTimeout,
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Root:UserName"] = PlatformAppWithRootAccount.RootUserName,
                ["Identity:Root:Password"] = PlatformAppWithRootAccount.RootPassword,
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock)
                .AddSingleton<IIntegrationEventSerializer>(new KnownPayloadSerializer(new SystemTextJsonIntegrationEventSerializer())
                { UseKnownPayload = value => value is SettingCommittedV1 or IdentityEntityCommittedV1 or StoredFileCommittedV1 or PlanCommittedV1 }));
        }
    }
}
