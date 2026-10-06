using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Endpoints;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FilesRecoveryMaintenanceTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PostgresModule_FailedBackgroundExpiryPreservesReceiptsAndPrivateContent_ThenRecoversInFiniteBatches()
    {
        await using var database = await databases.CreateAsync();
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(acceptedAt);
        var storageRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nsn-files-pg-recovery-maintenance-" + Guid.NewGuid().ToString("N")));
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(storageRoot), expectedParent, comparison))
        { throw new InvalidOperationException("Test storage escaped its owned temporary directory."); }
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:Storage:Provider"] = "Postgres",
                ["ConnectionStrings:Files"] = database.ConnectionString,
                ["Files:StorageRoot"] = storageRoot,
                ["Files:AuditDelivery:Cleanup:Enabled"] = "false",
                ["Files:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Files:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
                ["Files:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
                ["Files:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
            });
            builder.Services.AddNexusStackApplication();
            builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
            builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("file-owner"));
            builder.Services.AddSingleton<IClock>(clock);
            builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
            builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
            builder.Services.AddFilesModule(builder.Configuration, builder.Environment);
            await using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<FileService>();
            using var content = new MemoryStream([8, 4, 2]);
            var uploaded = await service.UploadAsync(FileName.Create("maintenance-private.bin").Value,
                "application/octet-stream", content, "file-owner");
            Assert.True(uploaded.IsSuccess);
            var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
            var beforeFile = await files.FindAsync(uploaded.Value.Id);
            Assert.NotNull(beforeFile);
            var delivery = scope.ServiceProvider.GetRequiredService<IFileAuditDelivery>();
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
            var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("files");
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
            var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
            Assert.Equal(2, originals.Count);
            var receipts = new List<FactDeliveryRecoveryReceipt>();
            foreach (var original in originals)
            {
                Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-maintenance-stop", acceptedAt, 0));
                var recovered = await delivery.RecoverAsync(new(Guid.NewGuid(), original.Id, acceptedAt, 0, "manual-retry"),
                    "maintenance-operator", acceptedAt, null);
                Assert.True(recovered.IsSuccess);
                receipts.Add(recovered.Value);
            }
            var beforeRecovery = (await delivery.ReadRecoveryCapacityAsync()).Value;
            Assert.Equal(2, beforeRecovery.Capacity.RetainedRecords);
            var beforeFacts = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
            var beforeBusiness = (await business.ReadAsync()).Value;
            var beforePolicy = (await policies.ReadPolicyAsync()).Value;
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var fault = await FactRecoveryMaintenanceFault.InstallAsync(connection, "files");
            clock.Advance(TimeSpan.FromDays(7));
            await host.StartAsync();
            try
            {
                var health = host.Services.GetRequiredService<HealthCheckService>();
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var diagnostic = await fault.WaitForFailureAsync(health, budget.Token);
                Assert.True((long)diagnostic.Data["cleanupFailures"] > 0);
                Assert.True((bool)diagnostic.Data["cleanupDegraded"]);
                Assert.Equal(0L, diagnostic.Data["releasedRequests"]);
                Assert.Null(diagnostic.Exception);
                Assert.DoesNotContain("Sensitive controlled maintenance failure", diagnostic.Description!, StringComparison.Ordinal);
                var readiness = await health.CheckHealthAsync(registration => !registration.Tags.Contains("auditing-diagnostics", StringComparer.Ordinal), budget.Token);
                Assert.NotEmpty(readiness.Entries);
                Assert.Equal(HealthStatus.Healthy, readiness.Status);
                foreach (var receipt in receipts)
                { Assert.Equal(receipt, (await delivery.GetRecoveryAsync(receipt.RequestId)).Value); }
                Assert.Equal(beforeRecovery, (await delivery.ReadRecoveryCapacityAsync()).Value);
                await AssertFileAndFactsAsync();
                await fault.AllowSingleReleaseAsync();
                diagnostic = await fault.WaitForReleaseAsync(health, 2, budget.Token);
                Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
                Assert.False((bool)diagnostic.Data["cleanupDegraded"]);
                Assert.True((long)diagnostic.Data["cleanupRuns"] >= 2);
                var after = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
                Assert.Equal(0, after.RetainedRecords);
                Assert.Equal(0, after.RetainedPayloadBytes);
                foreach (var receipt in receipts)
                { Assert.Equal("files.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(receipt.RequestId)).Error.Code); }
                await AssertFileAndFactsAsync();
            }
            finally { await host.StopAsync(); }

            async Task AssertFileAndFactsAsync()
            {
                Assert.Equal(beforeFacts, await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
                Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
                Assert.Equal(beforePolicy, (await policies.ReadPolicyAsync()).Value);
                var retained = await files.FindAsync(beforeFile.Id);
                Assert.NotNull(retained);
                Assert.Equal(beforeFile.Version, retained.Version);
                Assert.Equal(beforeFile.StorageKey, retained.StorageKey);
                Assert.Equal(beforeFile.OwnerId, retained.OwnerId);
                Assert.Equal(beforeFile.UpdatedAt, retained.UpdatedAt);
                Assert.Equal(beforeFile.Size, retained.Size);
                var opened = await service.OpenAsync(beforeFile.Id, "file-owner");
                Assert.True(opened.IsSuccess);
                await using var stream = opened.Value.Content;
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                Assert.Equal(new byte[] { 8, 4, 2 }, bytes.ToArray());
                Assert.Equal("files.not_found", (await service.OpenAsync(beforeFile.Id, "another-owner")).Error.Code);
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot)) { Directory.Delete(storageRoot, recursive: true); }
        }
    }

    [Fact]
    public async Task MemoryModule_BoundedRecoveryPoolRejectsAnotherRequest_AndBackgroundExpiryPreservesTheFile()
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(acceptedAt);
        var storageRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nsn-files-recovery-maintenance-" + Guid.NewGuid().ToString("N")));
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(storageRoot), expectedParent, comparison))
        { throw new InvalidOperationException("Test storage escaped its owned temporary directory."); }
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:Storage:Provider"] = "Memory",
                ["Files:StorageRoot"] = storageRoot,
                ["Files:AuditDelivery:Cleanup:Enabled"] = "false",
                ["Files:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Files:AuditDelivery:MemoryRecoveryControl:MaxRecords"] = "1",
                ["Files:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
                ["Files:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
                ["Files:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
            });
            builder.Services.AddNexusStackApplication();
            builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
            builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("file-owner"));
            builder.Services.AddSingleton<IClock>(clock);
            builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
            builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
            builder.Services.AddFilesModule(builder.Configuration, builder.Environment);
            await using var host = builder.Build();
            var health = host.Services.GetRequiredService<HealthCheckService>();
            var initial = await health.CheckHealthAsync(registration => registration.Name == "files-recovery-cleanup");
            Assert.True((bool)Assert.Single(initial.Entries).Value.Data["enabled"]);
            await using var scope = host.Services.CreateAsyncScope();
            var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
            var file = StoredFile.Register(new StoredFileId(99001), FileName.Create("maintenance.bin").Value,
                "application/octet-stream", "file-owner", acceptedAt).Value;
            Assert.True(file.MarkStored("maintenance-owned-storage", 3).IsSuccess);
            await files.SaveAsync(file);
            var beforeFile = await files.FindAsync(file.Id);
            Assert.NotNull(beforeFile);
            var delivery = scope.ServiceProvider.GetRequiredService<IFileAuditDelivery>();
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
            var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("files");
            var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
            Assert.Equal(2, originals.Count);
            foreach (var original in originals)
            { Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-maintenance-stop", acceptedAt, 0)); }
            var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), originals[0].Id, acceptedAt, 0, "manual-retry");
            var accepted = await delivery.RecoverAsync(request, "maintenance-operator", acceptedAt, null);
            Assert.True(accepted.IsSuccess);
            var counted = (await delivery.ReadRecoveryCapacityAsync()).Value;
            Assert.Equal(1, counted.Capacity.MaxRecords);
            Assert.Equal(1, counted.Capacity.RetainedRecords);
            var another = new FactDeliveryRecoveryRequest(Guid.NewGuid(), originals[1].Id, acceptedAt, 0, "manual-retry");
            Assert.Equal("files.delivery_recovery.exhausted", (await delivery.RecoverAsync(another, "maintenance-operator", acceptedAt, null)).Error.Code);
            Assert.Equal(counted, (await delivery.ReadRecoveryCapacityAsync()).Value);
            Assert.Equal("files.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(another.RequestId)).Error.Code);
            var beforeFacts = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
            var beforeBusiness = (await business.ReadAsync()).Value;
            clock.Advance(TimeSpan.FromDays(7));
            await host.StartAsync();
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                HealthReportEntry diagnostic;
                while (true)
                {
                    var report = await health.CheckHealthAsync(registration => registration.Name == "files-recovery-cleanup", budget.Token);
                    diagnostic = Assert.Single(report.Entries).Value;
                    if ((long)diagnostic.Data["releasedRequests"] == 1) { break; }
                    await Task.Delay(50, budget.Token);
                }
                Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
                Assert.Equal(0L, diagnostic.Data["cleanupFailures"]);
                var after = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
                Assert.Equal(0, after.RetainedRecords);
                Assert.Equal(0, after.RetainedPayloadBytes);
                Assert.Equal("files.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
                Assert.Equal(beforeFacts, await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
                Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
                var retainedFile = await files.FindAsync(file.Id);
                Assert.NotNull(retainedFile);
                Assert.Equal(beforeFile.Version, retainedFile.Version);
                Assert.Equal(beforeFile.StorageKey, retainedFile.StorageKey);
                Assert.Equal(beforeFile.UpdatedAt, retainedFile.UpdatedAt);
                Assert.True((await delivery.RecoverAsync(another, "maintenance-operator", clock.UtcNow, null)).IsSuccess);
            }
            finally { await host.StopAsync(); }
        }
        finally
        {
            if (Directory.Exists(storageRoot)) { Directory.Delete(storageRoot, recursive: true); }
        }
    }
}
