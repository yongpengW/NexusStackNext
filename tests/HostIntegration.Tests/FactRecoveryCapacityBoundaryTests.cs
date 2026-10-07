using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactRecoveryCapacityBoundaryTests(JourneyDatabaseTemplates databases)
{
    [Theory]
    [InlineData("platform", false)]
    [InlineData("identity", false)]
    [InlineData("files", false)]
    [InlineData("scheduling", false)]
    [InlineData("platform", true)]
    [InlineData("identity", true)]
    [InlineData("files", true)]
    [InlineData("scheduling", true)]
    public async Task PlatformHostMemory_OversizeAndFullPoolRejectWholeRecovery_AndExpiryReleasesOnlyEvidence(string source, bool recordLimit)
    {
        await using var app = new ByteControlApp(source, recordLimit) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, source, recordLimit);
    }

    [PostgresFact]
    public Task PlatformPostgres_OversizeAndFullBytePoolRejectWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyPlatformPostgresAsync("platform");

    [PostgresFact]
    public Task IdentityPostgres_OversizeAndFullBytePoolRejectWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyPlatformPostgresAsync("identity");

    [PostgresFact]
    public Task FilesPostgres_OversizeAndFullBytePoolRejectWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyPlatformPostgresAsync("files");

    [PostgresFact]
    public Task SchedulingPostgres_OversizeAndFullBytePoolRejectWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyPlatformPostgresAsync("scheduling");

    [PostgresFact]
    public Task CostingPostgres_OversizeAndFullBytePoolRejectWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyBusinessPostgresAsync("costing");

    [PostgresFact]
    public Task PricingPostgres_OversizeAndFullBytePoolRejectWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyBusinessPostgresAsync("pricing");

    [PostgresFact]
    public Task PlatformPostgres_FullRecordPoolRejectsWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyPlatformPostgresAsync("platform", recordLimit: true);

    [PostgresFact]
    public Task IdentityPostgres_FullRecordPoolRejectsWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyPlatformPostgresAsync("identity", recordLimit: true);

    [PostgresFact]
    public Task FilesPostgres_FullRecordPoolRejectsWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyPlatformPostgresAsync("files", recordLimit: true);

    [PostgresFact]
    public Task SchedulingPostgres_FullRecordPoolRejectsWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyPlatformPostgresAsync("scheduling", recordLimit: true);

    [PostgresFact]
    public Task CostingPostgres_FullRecordPoolRejectsWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyBusinessPostgresAsync("costing", recordLimit: true);

    [PostgresFact]
    public Task PricingPostgres_FullRecordPoolRejectsWholeRecovery_AndExpiryReleasesOnlyEvidence()
        => VerifyBusinessPostgresAsync("pricing", recordLimit: true);

    private async Task VerifyPlatformPostgresAsync(string source, bool recordLimit = false)
    {
        await using var database = await databases.CreateAsync();
        await SetOwnedByteEnvelopeAsync(database.ConnectionString, source, recordLimit);
        await using var app = new PersistentIdentityApp(database.ConnectionString, PlatformAppWithRootAccount.RootPassword,
            schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, source, recordLimit);
    }

    private async Task VerifyBusinessPostgresAsync(string source, bool recordLimit = false)
    {
        await using var database = await databases.CreateAsync(source);
        await SetOwnedByteEnvelopeAsync(database.ConnectionString, source, recordLimit);
        var assembly = source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
        await using var host = await BusinessProcess.StartAsync(assembly, source == "costing" ? "Costing" : "Pricing", database.ConnectionString);
        host.Authenticate();
        await using var reader = source == "costing" ? TaskOperationTests.CreateCostingApp(database.ConnectionString, null)
            : TaskOperationTests.CreatePricingApp(database.ConnectionString, null);
        await VerifyAsync(host.Client, reader.Services, source, recordLimit);
    }

    private static async Task SetOwnedByteEnvelopeAsync(string connectionString, string source, bool recordLimit)
    {
        Assert.Contains(source, new[] { "platform", "identity", "files", "scheduling", "costing", "pricing" });
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // Arrange a smaller envelope only in this test's independently owned database.
        await using var configure = new NpgsqlCommand($"""
            UPDATE {source}.fact_recovery_control SET "MaxRecords" = @records,
                "MaxPayloadBytes" = @bytes, "MaxRecordPayloadBytes" = 1024
            WHERE "Id" = 1
            """, connection);
        configure.Parameters.AddWithValue("records", recordLimit ? 1L : 1000L);
        configure.Parameters.AddWithValue("bytes", recordLimit ? 16L * 1024 * 1024 : 1024L);
        Assert.Equal(1, await configure.ExecuteNonQueryAsync());
    }

    private static async Task VerifyAsync(HttpClient client, IServiceProvider services, string source, bool recordLimit)
    {
        var policyPath = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        var messages = new List<Guid>();
        for (var round = 0; round < 2; round++)
        {
            using var initial = await client.GetAsync(policyPath);
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            var policy = await initial.Content.ReadApiDataAsync();
            using var changed = await client.PutAsJsonAsync(policyPath, new
            {
                requestId = Guid.NewGuid(),
                expectedPolicyRevision = policy.GetProperty("policyRevision").GetString(),
                maxRecords = (policy.GetProperty("maxRecords").ReadHttpInt64() + 1).ToString(CultureInfo.InvariantCulture),
                maxPayloadBytes = policy.GetProperty("maxPayloadBytes").GetString(),
                maxRecordPayloadBytes = policy.GetProperty("maxRecordPayloadBytes").GetInt32(),
                reason = "operator-adjustment",
            });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            messages.Add((await changed.Content.ReadApiDataAsync()).GetProperty("eventId").GetGuid());
        }
        using var beforeResponse = await client.GetAsync(policyPath);
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
        var businessBefore = await beforeResponse.Content.ReadApiDataAsync();
        await using var scope = services.CreateAsyncScope();
        var outbox = FactRecoveryProtocolTests.GetOutbox(scope.ServiceProvider, source);
        var delivery = FactRecoveryProtocolTests.GetPort(scope.ServiceProvider, source);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        foreach (var message in messages)
        { Assert.True(await outbox.MarkDeadLetteredAsync(message, "controlled-byte-stop", now, 0)); }
        var firstState = (await delivery.GetAsync(messages[0])).Value;
        var secondState = (await delivery.GetAsync(messages[1])).Value;
        var empty = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(recordLimit ? 1 : 1000, empty.Capacity.MaxRecords);
        Assert.Equal(recordLimit ? 16 * 1024 * 1024 : 1024, empty.Capacity.MaxPayloadBytes);
        Assert.Equal(1024, empty.Capacity.MaxRecordPayloadBytes);
        Assert.Equal(0, empty.Capacity.RetainedRecords);
        Assert.Equal(0, empty.Capacity.RetainedPayloadBytes);
        var first = new FactDeliveryRecoveryRequest(Guid.NewGuid(), messages[0], now, 0, "manual-retry");
        var second = new FactDeliveryRecoveryRequest(Guid.NewGuid(), messages[1], now, 0, "manual-retry");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                delivery.RecoverAsync(first, "cancelled-operator", now, null, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                delivery.CleanupRecoveriesAsync(1, now.AddDays(7), cancelled.Token));
        }
        Assert.Equal(empty, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(firstState, (await delivery.GetAsync(messages[0])).Value);
        Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(first.RequestId)).Error.Code);
        Assert.Equal($"{source}.delivery_recovery.exhausted",
            (await delivery.RecoverAsync(first, new string('操', 200), now, null)).Error.Code);
        Assert.Equal(empty, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(firstState, (await delivery.GetAsync(messages[0])).Value);
        Assert.Equal(secondState, (await delivery.GetAsync(messages[1])).Value);
        Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(first.RequestId)).Error.Code);

        var actor = new string('a', 200);
        var accepted = await delivery.RecoverAsync(first, actor, now, null);
        Assert.True(accepted.IsSuccess);
        var counted = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(1, counted.Capacity.RetainedRecords);
        Assert.InRange(counted.Capacity.RetainedPayloadBytes, 513, 1024);
        Assert.Equal($"{source}.delivery_recovery.exhausted", (await delivery.RecoverAsync(second, actor, now, null)).Error.Code);
        Assert.Equal(counted, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(secondState, (await delivery.GetAsync(messages[1])).Value);
        Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(second.RequestId)).Error.Code);
        Assert.Equal(accepted.Value, (await delivery.RecoverAsync(first, actor, now.AddDays(1), null)).Value);
        Assert.Equal(counted, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(0, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7).AddTicks(-1)));
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7)));
        Assert.Equal(empty, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.True((await delivery.RecoverAsync(second, actor, now.AddDays(7), null)).IsSuccess);
        Assert.Equal(1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        var pending = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Contains(pending, message => message.Id == messages[0]);
        Assert.Contains(pending, message => message.Id == messages[1]);
        using var afterResponse = await client.GetAsync(policyPath);
        Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
        Assert.True(JsonElement.DeepEquals(businessBefore, await afterResponse.Content.ReadApiDataAsync()));
    }

    private sealed class ByteControlApp(string source, bool recordLimit) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var context = char.ToUpperInvariant(source[0]) + source[1..];
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{context}:AuditDelivery:MemoryRecoveryControl:MaxRecords"] = recordLimit ? "1" : "1000",
                [$"{context}:AuditDelivery:MemoryRecoveryControl:MaxPayloadBytes"] = recordLimit ? "16777216" : "1024",
                [$"{context}:AuditDelivery:MemoryRecoveryControl:MaxRecordPayloadBytes"] = "1024",
                [$"{context}:AuditDelivery:RecoveryMaintenance:Enabled"] = "false",
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
        }
    }
}
