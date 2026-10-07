using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Endpoints;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Endpoints;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Endpoints;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Endpoints;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactRecoveryPendingWorkTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public Task Scheduling_RecoveryCommitsIndependently_WithoutSavingOrDiscardingTheCallersPendingPlanAndOrigin()
        => VerifyPendingPlanAsync("Postgres");

    [Fact]
    public Task SchedulingMemory_BusyRecoveryPreservesTheCallersPendingPlanAndOrigin_AndCanRecoverAfterCommit()
        => VerifyPendingPlanAsync("Memory");

    private async Task VerifyPendingPlanAsync(string provider)
    {
        await using var database = provider == "Postgres" ? await databases.CreateAsync() : null;
        var instant = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        using var serializer = new PausingEventSerializer(new SystemTextJsonIntegrationEventSerializer(), PlanCommittedV1.Name);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scheduling:Storage:Provider"] = provider,
            ["ConnectionStrings:Scheduling"] = database?.ConnectionString,
            ["Scheduling:AuditDelivery:CapacityWrite:Timeout"] = "00:00:00.150",
            ["Scheduling:Worker:Enabled"] = "false",
            ["Scheduling:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Scheduling:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Scheduling:AuditDelivery:RecoveryMaintenance:Enabled"] = "false",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("plan-owner"));
        builder.Services.AddSingleton<IClock>(new FixedClock(instant));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(serializer);
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var plans = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("scheduling");
        var initial = (await policies.ReadPolicyAsync()).Value;
        Assert.True((await policies.AdjustAsync(new(Guid.NewGuid(), initial.PolicyRevision, initial.MaxRecords + 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment"), "policy-operator", instant, null)).IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(SchedulingFactCapacityPolicyChangedV1.Name, original.EventName);
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-pending-work-stop", instant, 0));
        var defined = ScheduledTask.Create(new ScheduledTaskId(99502), TaskCode.Create("pending-recovery-plan").Value,
            TimeSpan.FromMinutes(1), instant,
            ScheduleTarget.Create("costing.recalculate", Guid.Parse("11111111-2222-3333-4444-555555555555")).Value, "plan-owner");
        Assert.True(defined.IsSuccess);
        var operationId = Guid.Parse("22222222-3333-4444-5555-666666666666");
        var origin = new ExecutionOrigin(operationId, "scheduling", operationId, "scheduling", "plan-owner", operationId.ToString("N"));
        Assert.True(origin.IsValid());
        var recovery = new PendingRecovery(app.Services, scope.ServiceProvider, "scheduling", instant, original);
        if (provider == "Memory")
        {
            await recovery.VerifyBusyAsync(serializer, token => plans.AddAsync(defined.Value, origin, token),
                result => Assert.True(result.IsSuccess), AssertPlanUncommittedAsync);
        }
        else
        {
            await recovery.VerifyAsync(serializer, token => plans.AddAsync(defined.Value, origin, token),
                result => Assert.True(result.IsSuccess), AssertPlanUncommittedAsync);
        }
        await using var committed = app.Services.CreateAsyncScope();
        var committedPlans = committed.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var saved = await committedPlans.FindAsync(defined.Value.Id);
        Assert.NotNull(saved);
        Assert.Equal(defined.Value.Version, saved.Version);
        Assert.Equal(defined.Value.Code, saved.Code);
        Assert.Equal(defined.Value.Rule, saved.Rule);
        Assert.Equal(defined.Value.NextRunAt, saved.NextRunAt);
        Assert.Equal(defined.Value.Target, saved.Target);
        Assert.Equal(defined.Value.DelegatedBy, saved.DelegatedBy);
        var retainedOrigin = await committedPlans.ReadExecutionOriginAsync(saved.Id);
        Assert.NotNull(retainedOrigin);
        Assert.Equal(origin, retainedOrigin);
        Assert.Empty((await committedPlans.ReadDecisionsAsync(saved.Id.Value, 0, 10)).Items);
        Assert.Empty((await committedPlans.ReadOccurrencesAsync(saved.Id.Value, 0, 10)).Items);
        var facts = await committed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling").ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, facts.Count);
        Assert.Single(facts, entry => entry.Id == original.Id && entry.Payload == original.Payload && entry.RetryRevision == 1);
        Assert.Single(facts, entry => entry.EventName == PlanCommittedV1.Name);

        async Task AssertPlanUncommittedAsync(CancellationToken cancellationToken)
        {
            await using var observer = app.Services.CreateAsyncScope();
            var observed = observer.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
            Assert.Null(await observed.FindAsync(defined.Value.Id, cancellationToken));
            Assert.Null(await observed.ReadExecutionOriginAsync(defined.Value.Id, cancellationToken));
        }
    }

    [PostgresFact]
    public Task Files_RecoveryCommitsIndependently_WithoutSavingOrDiscardingTheCallersPendingPrivateUpload()
        => VerifyPendingFileAsync("Postgres");

    [Fact]
    public Task FilesMemory_BusyRecoveryPreservesTheCallersPendingPrivateUpload_AndCanRecoverAfterCommit()
        => VerifyPendingFileAsync("Memory");

    private async Task VerifyPendingFileAsync(string provider)
    {
        await using var database = provider == "Postgres" ? await databases.CreateAsync() : null;
        var instant = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        using var serializer = new PausingEventSerializer(new SystemTextJsonIntegrationEventSerializer(), StoredFileCommittedV1.Name);
        var storageRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nsn-files-pending-recovery-" + Guid.NewGuid().ToString("N")));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(storageRoot), parent, comparison))
        { throw new InvalidOperationException("Test storage escaped its owned temporary directory."); }
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:Storage:Provider"] = provider,
                ["ConnectionStrings:Files"] = database?.ConnectionString,
                ["Files:AuditDelivery:CapacityWrite:Timeout"] = "00:00:00.150",
                ["Files:StorageRoot"] = storageRoot,
                ["Files:AuditDelivery:Cleanup:Enabled"] = "false",
                ["Files:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Files:AuditDelivery:RecoveryMaintenance:Enabled"] = "false",
            });
            builder.Services.AddNexusStackApplication();
            builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(99500));
            builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("file-owner"));
            builder.Services.AddSingleton<IClock>(new FixedClock(instant));
            builder.Services.AddSingleton<IIntegrationEventSerializer>(serializer);
            builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
            builder.Services.AddFilesModule(builder.Configuration, builder.Environment);
            await using var app = builder.Build();
            await using var scope = app.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<FileService>();
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
            var initial = (await policies.ReadPolicyAsync()).Value;
            Assert.True((await policies.AdjustAsync(new(Guid.NewGuid(), initial.PolicyRevision, initial.MaxRecords + 1,
                initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment"), "policy-operator", instant, null)).IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
            var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
            Assert.Equal(FilesFactCapacityPolicyChangedV1.Name, original.EventName);
            Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-pending-work-stop", instant, 0));
            var fileId = new StoredFileId(99501);
            using var content = new MemoryStream([8, 4, 2]);
            var recovery = new PendingRecovery(app.Services, scope.ServiceProvider, "files", instant, original);
            var uploaded = provider == "Memory"
                ? await recovery.VerifyBusyAsync(serializer, token => service.UploadAsync(FileName.Create("pending-private.bin").Value,
                    "application/octet-stream", content, "file-owner", token), result => Assert.True(result.IsSuccess), AssertFileUncommittedAsync)
                : await recovery.VerifyAsync(serializer, token => service.UploadAsync(FileName.Create("pending-private.bin").Value,
                    "application/octet-stream", content, "file-owner", token), result => Assert.True(result.IsSuccess), AssertFileUncommittedAsync);
            Assert.Equal(fileId, uploaded.Value.Id);
            await using var committed = app.Services.CreateAsyncScope();
            var saved = await committed.ServiceProvider.GetRequiredService<IStoredFileRepository>().FindAsync(fileId);
            Assert.NotNull(saved);
            Assert.True(saved.IsStored);
            Assert.Equal("file-owner", saved.OwnerId);
            Assert.Equal(3, saved.Size);
            var committedService = committed.ServiceProvider.GetRequiredService<FileService>();
            var opened = await committedService.OpenAsync(fileId, "file-owner");
            Assert.True(opened.IsSuccess);
            await using var stream = opened.Value.Content;
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            Assert.Equal(new byte[] { 8, 4, 2 }, bytes.ToArray());
            Assert.Equal("files.not_found", (await committedService.OpenAsync(fileId, "another-owner")).Error.Code);
            var facts = await committed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files").ReadPendingAsync(100, DateTimeOffset.MaxValue);
            Assert.Equal(3, facts.Count);
            Assert.Single(facts, entry => entry.Id == original.Id && entry.Payload == original.Payload && entry.RetryRevision == 1);
            Assert.Equal(2, facts.Count(entry => entry.EventName == StoredFileCommittedV1.Name));

            async Task AssertFileUncommittedAsync(CancellationToken cancellationToken)
            {
                await using var observer = app.Services.CreateAsyncScope();
                Assert.Null(await observer.ServiceProvider.GetRequiredService<IStoredFileRepository>().FindAsync(fileId, cancellationToken));
                Assert.Equal("files.not_found", (await observer.ServiceProvider.GetRequiredService<FileService>()
                    .OpenAsync(fileId, "file-owner", cancellationToken)).Error.Code);
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot)) { Directory.Delete(storageRoot, recursive: true); }
        }
    }

    [PostgresFact]
    public Task Platform_RecoveryCommitsIndependently_WithoutSavingOrDiscardingTheCallersPendingSetting()
        => VerifyPendingSettingAsync("Postgres");

    [Fact]
    public Task PlatformMemory_RecoveryCommitsIndependently_WithoutSavingOrDiscardingTheCallersPendingSetting()
        => VerifyPendingSettingAsync("Memory");

    private async Task VerifyPendingSettingAsync(string provider)
    {
        await using var database = provider == "Postgres" ? await databases.CreateAsync() : null;
        var instant = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        using var serializer = new PausingEventSerializer(new SystemTextJsonIntegrationEventSerializer(), SettingCommittedV1.Name);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Storage:Provider"] = provider,
            ["ConnectionStrings:Platform"] = database?.ConnectionString,
            ["Platform:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Platform:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Platform:AuditDelivery:RecoveryMaintenance:Enabled"] = "false",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("setting-owner"));
        builder.Services.AddSingleton<IClock>(new FixedClock(instant));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(serializer);
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddPlatformModule(builder.Configuration, builder.Environment);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("platform");
        var initial = (await policies.ReadPolicyAsync()).Value;
        Assert.True((await policies.AdjustAsync(new(Guid.NewGuid(), initial.PolicyRevision, initial.MaxRecords + 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment"), "policy-operator", instant, null)).IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(SettingFactCapacityPolicyChangedV1.Name, original.EventName);
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-pending-work-stop", instant, 0));
        var key = SettingKey.Create("recovery.pending-setting").Value;
        await new PendingRecovery(app.Services, scope.ServiceProvider, "platform", instant, original)
            .VerifyAsync(serializer, token => settings.WriteAsync(key, "private-pending-value", expectedVersion: 0, cancellationToken: token),
                result => Assert.True(result.IsSuccess), AssertSettingUncommittedAsync);
        await using var committed = app.Services.CreateAsyncScope();
        var saved = await committed.ServiceProvider.GetRequiredService<SettingStore>().GetAsync(key);
        Assert.NotNull(saved);
        Assert.Equal("private-pending-value", saved.Value);
        Assert.Equal(1, saved.Version);
        var facts = await committed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform").ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, facts.Count);
        Assert.Single(facts, entry => entry.Id == original.Id && entry.Payload == original.Payload && entry.RetryRevision == 1);
        Assert.Single(facts, entry => entry.EventName == SettingCommittedV1.Name);

        async Task AssertSettingUncommittedAsync(CancellationToken cancellationToken)
        {
            await using var observer = app.Services.CreateAsyncScope();
            Assert.Null(await observer.ServiceProvider.GetRequiredService<SettingStore>().GetAsync(key, cancellationToken));
        }
    }

    [PostgresFact]
    public async Task Costing_RecoveryCommitsIndependently_WithoutSavingOrDiscardingTheCallersPendingCostAndTask()
    {
        await using var database = await databases.CreateAsync("costing");
        var instant = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        using var serializer = new PausingEventSerializer(new SystemTextJsonIntegrationEventSerializer(), CostSheetCommittedV1.Name);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Costing"] = database.ConnectionString,
            ["Costing:Worker:Enabled"] = "false",
            ["Costing:Messaging:Enabled"] = "false",
            ["Costing:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Costing:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Costing:AuditDelivery:RecoveryMaintenance:Enabled"] = "false",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("cost-owner"));
        builder.Services.AddSingleton<IClock>(new FixedClock(instant));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(serializer);
        builder.Services.AddCostingModule(builder.Configuration);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("costing");
        var initialPolicy = (await policies.ReadPolicyAsync()).Value;
        var changed = await policies.AdjustAsync(new(Guid.NewGuid(), initialPolicy.PolicyRevision, initialPolicy.MaxRecords + 1,
            initialPolicy.MaxPayloadBytes, initialPolicy.MaxRecordPayloadBytes, "operator-adjustment"), "policy-operator", instant, null);
        Assert.True(changed.IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(CostingFactCapacityPolicyChangedV1.Name, original.EventName);
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-pending-work-stop", instant, 0));
        var command = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        await new PendingRecovery(app.Services, scope.ServiceProvider, "costing", instant, original)
            .VerifyAsync(serializer, token => sender.SendAsync(command, token),
                result => Assert.True(result.IsSuccess), AssertBusinessStillUncommittedAsync);
        await using var committed = app.Services.CreateAsyncScope();
        var committedSender = committed.ServiceProvider.GetRequiredService<ISender>();
        var sheet = await committedSender.QueryAsync(new GetCostSheet(command.ItemId));
        Assert.True(sheet.IsSuccess);
        Assert.Equal(80m, sheet.Value.PurchaseCost);
        Assert.Equal(20m, sheet.Value.FreightCost);
        Assert.Equal(1, sheet.Value.InputRevision);
        var task = await committedSender.QueryAsync(new GetCostCalculation(command.RequestId));
        Assert.True(task.IsSuccess);
        Assert.Equal("Pending", task.Value.State);
        Assert.Equal(command.ItemId, task.Value.ItemId);
        Assert.Equal(1, task.Value.InputRevision);
        var facts = await committed.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, facts.Count);
        Assert.Single(facts, entry => entry.Id == original.Id && entry.Payload == original.Payload && entry.RetryRevision == 1);
        Assert.Single(facts, entry => entry.EventName == CostSheetCommittedV1.Name);

        async Task AssertBusinessStillUncommittedAsync(CancellationToken cancellationToken)
        {
            await using var observer = app.Services.CreateAsyncScope();
            var observerSender = observer.ServiceProvider.GetRequiredService<ISender>();
            Assert.Equal("costing.not_found", (await observerSender.QueryAsync(new GetCostSheet(command.ItemId), cancellationToken)).Error.Code);
            Assert.Equal("costing.not_found", (await observerSender.QueryAsync(new GetCostCalculation(command.RequestId), cancellationToken)).Error.Code);
        }
    }
    [PostgresFact]
    public async Task Pricing_RecoveryCommitsIndependently_WithoutSavingOrDiscardingTheCallersPendingQuoteAndTask()
    {
        await using var database = await databases.CreateAsync("pricing");
        var instant = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        using var serializer = new PausingEventSerializer(new SystemTextJsonIntegrationEventSerializer(), PriceQuoteCommittedV1.Name);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Pricing"] = database.ConnectionString,
            ["Pricing:Worker:Enabled"] = "false",
            ["Pricing:Messaging:Enabled"] = "false",
            ["Pricing:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Pricing:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Pricing:AuditDelivery:RecoveryMaintenance:Enabled"] = "false",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("cost-owner"));
        builder.Services.AddSingleton<IClock>(new FixedClock(instant));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(serializer);
        builder.Services.AddPricingModule(builder.Configuration);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("pricing");
        var initialPolicy = (await policies.ReadPolicyAsync()).Value;
        var changed = await policies.AdjustAsync(new(Guid.NewGuid(), initialPolicy.PolicyRevision, initialPolicy.MaxRecords + 1,
            initialPolicy.MaxPayloadBytes, initialPolicy.MaxRecordPayloadBytes, "operator-adjustment"), "policy-operator", instant, null);
        Assert.True(changed.IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(PricingFactCapacityPolicyChangedV1.Name, original.EventName);
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-pending-work-stop", instant, 0));
        var command = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        await new PendingRecovery(app.Services, scope.ServiceProvider, "pricing", instant, original)
            .VerifyAsync(serializer, token => sender.SendAsync(command, token),
                result => Assert.True(result.IsSuccess), AssertBusinessStillUncommittedAsync);
        await using var committed = app.Services.CreateAsyncScope();
        var committedSender = committed.ServiceProvider.GetRequiredService<ISender>();
        var sheet = await committedSender.QueryAsync(new GetPriceQuote(command.ItemId));
        Assert.True(sheet.IsSuccess);
        Assert.Equal(80m, sheet.Value.Cost);
        Assert.Equal(0.2m, sheet.Value.FeeRate);
        Assert.Equal(1, sheet.Value.InputRevision);
        var task = await committedSender.QueryAsync(new GetRecalculation(command.RequestId));
        Assert.True(task.IsSuccess);
        Assert.Equal("Pending", task.Value.State);
        Assert.Equal(command.ItemId, task.Value.ItemId);
        Assert.Equal(1, task.Value.InputRevision);
        var facts = await committed.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, facts.Count);
        Assert.Single(facts, entry => entry.Id == original.Id && entry.Payload == original.Payload && entry.RetryRevision == 1);
        Assert.Single(facts, entry => entry.EventName == PriceQuoteCommittedV1.Name);

        async Task AssertBusinessStillUncommittedAsync(CancellationToken cancellationToken)
        {
            await using var observer = app.Services.CreateAsyncScope();
            var observerSender = observer.ServiceProvider.GetRequiredService<ISender>();
            Assert.Equal("pricing.not_found", (await observerSender.QueryAsync(new GetPriceQuote(command.ItemId), cancellationToken)).Error.Code);
            Assert.Equal("pricing.not_found", (await observerSender.QueryAsync(new GetRecalculation(command.RequestId), cancellationToken)).Error.Code);
        }
    }

    private sealed class PendingRecovery(IServiceProvider root, IServiceProvider caller, string source,
        DateTimeOffset instant, OutboxEntry original)
    {
        public async Task<TResult> VerifyBusyAsync<TResult>(PausingEventSerializer serializer,
            Func<CancellationToken, Task<TResult>> write, Action<TResult> assertSuccess,
            Func<CancellationToken, Task> assertInitiallyUncommitted)
        {
            var delivery = FactRecoveryProtocolTests.GetPort(caller, source);
            var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, instant, 0, "manual-retry");
            var stopped = (await delivery.GetAsync(original.Id)).Value;
            var beforeRecovery = (await delivery.ReadRecoveryCapacityAsync()).Value;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await assertInitiallyUncommitted(budget.Token);
            var result = await VerifyPausedWriteAsync(serializer, write, assertSuccess, budget, async token =>
            {
                var busy = await delivery.RecoverAsync(request, "recovery-operator", instant, null, token);
                Assert.Equal("audit_capacity.busy", busy.Error.Code);
            });
            Assert.Equal(stopped, (await delivery.GetAsync(original.Id)).Value);
            Assert.Equal(beforeRecovery, (await delivery.ReadRecoveryCapacityAsync()).Value);
            Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
            var beforePolicy = (await caller.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source).ReadPolicyAsync()).Value;
            var beforeBusiness = (await caller.GetRequiredKeyedService<ICommittedFactCapacityReader>(source).ReadAsync()).Value;
            var recovered = await delivery.RecoverAsync(request, "recovery-operator", instant, null);
            Assert.True(recovered.IsSuccess);
            Assert.Equal(recovered.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
            var pending = Assert.Single(await FactRecoveryProtocolTests.GetOutbox(caller, source)
                .ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == original.Id);
            Assert.Equal(original.Id, pending.Id);
            Assert.Equal(original.EventName, pending.EventName);
            Assert.Equal(original.Payload, pending.Payload);
            Assert.Equal(original.OccurredAt, pending.OccurredAt);
            Assert.Equal(1, pending.RetryRevision);
            Assert.Equal(beforeRecovery.Capacity.RetainedRecords + 1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
            Assert.Equal(beforePolicy, (await caller.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source).ReadPolicyAsync()).Value);
            Assert.Equal(beforeBusiness, (await caller.GetRequiredKeyedService<ICommittedFactCapacityReader>(source).ReadAsync()).Value);
            return result;
        }

        public async Task<TResult> VerifyAsync<TResult>(PausingEventSerializer serializer,
            Func<CancellationToken, Task<TResult>> write, Action<TResult> assertSuccess,
            Func<CancellationToken, Task> assertUncommitted)
        {
            var delivery = FactRecoveryProtocolTests.GetPort(caller, source);
            var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, instant, 0, "manual-retry");
            var beforePolicy = (await caller.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source).ReadPolicyAsync()).Value;
            var beforeBusiness = (await caller.GetRequiredKeyedService<ICommittedFactCapacityReader>(source).ReadAsync()).Value;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            return await VerifyPausedWriteAsync(serializer, write, assertSuccess, budget, async token =>
            {
                await assertUncommitted(token);
                var recovered = await delivery.RecoverAsync(request, "recovery-operator", instant, null, token);
                Assert.True(recovered.IsSuccess);
                await assertUncommitted(token);
                Assert.Equal(recovered.Value, (await delivery.Receipt(request.RequestId, token)).Value);
                await using var observer = root.CreateAsyncScope();
                var pending = Assert.Single(await FactRecoveryProtocolTests.GetOutbox(observer.ServiceProvider, source)
                    .ReadPendingAsync(100, DateTimeOffset.MaxValue, token));
                Assert.Equal(original.Id, pending.Id);
                Assert.Equal(original.EventName, pending.EventName);
                Assert.Equal(original.Payload, pending.Payload);
                Assert.Equal(original.OccurredAt, pending.OccurredAt);
                Assert.Equal(1, pending.RetryRevision);
                Assert.Equal(beforePolicy, (await observer.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source)
                    .ReadPolicyAsync(token)).Value);
                Assert.Equal(beforeBusiness, (await observer.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>(source)
                    .ReadAsync(token)).Value);
            });
        }

        private static async Task<TResult> VerifyPausedWriteAsync<TResult>(PausingEventSerializer serializer,
            Func<CancellationToken, Task<TResult>> write, Action<TResult> assertSuccess,
            CancellationTokenSource budget, Func<CancellationToken, Task> assertWhilePaused)
        {
            var writing = Task.Run(() => write(budget.Token), budget.Token);
            try
            {
                await serializer.Paused.WaitAsync(budget.Token);
                Assert.False(writing.IsCompleted);
                await assertWhilePaused(budget.Token);
                Assert.False(writing.IsCompleted);
                serializer.Resume();
                var result = await writing.WaitAsync(budget.Token);
                assertSuccess(result);
                return result;
            }
            finally
            {
                serializer.Resume();
                if (!writing.IsCompleted) { budget.Cancel(); }
                // Join the real writer before any caller scope, database, content stream or storage root is disposed.
                try { await writing; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
            }
        }
    }
}
