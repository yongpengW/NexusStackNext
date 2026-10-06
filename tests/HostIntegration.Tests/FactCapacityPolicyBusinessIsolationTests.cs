using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Endpoints;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Endpoints;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Endpoints;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Endpoints;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Endpoints;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactCapacityPolicyBusinessIsolationTests(JourneyDatabaseTemplates databases)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid[] ItemIds =
    [
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Guid.Parse("00000000-0000-0000-0000-000000000002"),
        Guid.Parse("00000000-0000-0000-0000-000000000003"),
    ];
    private static readonly Guid[] TaskIds =
    [
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("10000000-0000-0000-0000-000000000002"),
        Guid.Parse("10000000-0000-0000-0000-000000000003"),
    ];

    [Fact]
    public async Task PlatformMemory_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
    {
        await VerifyAsync("platform", false);
    }

    [PostgresFact]
    public async Task PlatformPostgres_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
    {
        await VerifyAsync("platform", true);
    }

    [Fact]
    public Task IdentityMemory_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
        => VerifyAsync("identity", false);

    [PostgresFact]
    public Task IdentityPostgres_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
        => VerifyAsync("identity", true);

    [Fact]
    public Task FilesMemory_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
        => VerifyAsync("files", false);

    [PostgresFact]
    public Task FilesPostgres_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
        => VerifyAsync("files", true);

    [Fact]
    public Task SchedulingMemory_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
        => VerifyAsync("scheduling", false);

    [PostgresFact]
    public Task SchedulingPostgres_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
        => VerifyAsync("scheduling", true);

    [PostgresFact]
    public Task CostingPostgres_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
        => VerifyAsync("costing", true);

    [PostgresFact]
    public Task PricingPostgres_LowerRecordAndByteLimits_PreservesExistingBusinessAndBackpressuresNewWrites()
        => VerifyAsync("pricing", true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PlatformMemory_SerializationFailureOrCancellation_PreservesBusinessPolicyAndRetry(bool cancel)
        => VerifyFailureAsync("platform", false, cancel);

    [PostgresFact]
    public Task PlatformPostgres_SerializationFailure_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("platform", true, false);

    [PostgresFact]
    public Task PlatformPostgres_CancellationDuringSerialization_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("platform", true, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task IdentityMemory_SerializationFailureOrCancellation_PreservesBusinessPolicyAndRetry(bool cancel)
        => VerifyFailureAsync("identity", false, cancel);

    [PostgresFact]
    public Task IdentityPostgres_SerializationFailure_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("identity", true, false);

    [PostgresFact]
    public Task IdentityPostgres_CancellationDuringSerialization_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("identity", true, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FilesMemory_SerializationFailureOrCancellation_PreservesBusinessPolicyAndRetry(bool cancel)
        => VerifyFailureAsync("files", false, cancel);

    [PostgresFact]
    public Task FilesPostgres_SerializationFailure_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("files", true, false);

    [PostgresFact]
    public Task FilesPostgres_CancellationDuringSerialization_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("files", true, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SchedulingMemory_SerializationFailureOrCancellation_PreservesBusinessPolicyAndRetry(bool cancel)
        => VerifyFailureAsync("scheduling", false, cancel);

    [PostgresFact]
    public Task SchedulingPostgres_SerializationFailure_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("scheduling", true, false);

    [PostgresFact]
    public Task SchedulingPostgres_CancellationDuringSerialization_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("scheduling", true, true);

    [PostgresFact]
    public Task CostingPostgres_SerializationFailure_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("costing", true, false);

    [PostgresFact]
    public Task CostingPostgres_CancellationDuringSerialization_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("costing", true, true);

    [PostgresFact]
    public Task PricingPostgres_SerializationFailure_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("pricing", true, false);

    [PostgresFact]
    public Task PricingPostgres_CancellationDuringSerialization_PreservesBusinessPolicyAndRetry()
        => VerifyFailureAsync("pricing", true, true);

    private async Task VerifyAsync(string context, bool postgres)
    {
        await using var database = postgres ? await databases.CreateAsync(context) : null;
        await using var app = CreateApp(context, database?.ConnectionString);
        await VerifyLoweringAsync(app, context);
    }


    private async Task VerifyFailureAsync(string context, bool postgres, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var actual = new SystemTextJsonIntegrationEventSerializer();
        var rejecting = new RejectingEventSerializer(actual)
        { ShouldReject = fact => fact.EventName == context + ".fact-capacity-policy-changed.v1" };
        var canceling = new CancelingEventSerializer(actual)
        {
            ShouldCancel = fact => fact.EventName == context + ".fact-capacity-policy-changed.v1",
            CancelOnSerialize = cancellation,
        };
        IIntegrationEventSerializer selected = cancel ? canceling : rejecting;
        await using var database = postgres ? await databases.CreateAsync(context) : null;
        await using var app = CreateApp(context, database?.ConnectionString, selected);
        Assert.True((await WriteAsync(app, context, 1)).IsSuccess);
        Assert.True((await WriteAsync(app, context, 2)).IsSuccess);
        var business = await ReadBusinessAsync(app, context);
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
        var outbox = OwnedOutbox(scope.ServiceProvider, context);
        var before = (await policies.ReadPolicyAsync()).Value;
        var facts = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(context == "files" ? 4 : 2, before.RetainedRecords);
        Assert.Equal(0, before.ControlCapacity.RetainedRecords);
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, before.MaxRecords + 1,
            before.MaxPayloadBytes, before.MaxRecordPayloadBytes, "operator-adjustment");
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policies.AdjustAsync(request, "42", Now, null, cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => policies.AdjustAsync(request, "42", Now, null));
            Assert.Equal("测试事实序列化故障。", error.Message);
        }
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(facts, await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(business, await ReadBusinessAsync(app, context));
        rejecting.ShouldReject = null;
        canceling.CancelOnSerialize = null;
        var retried = await policies.AdjustAsync(request, "42", Now, null);
        Assert.True(retried.IsSuccess);
        Assert.Equal(2, retried.Value.PolicyRevision);
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1, after.ControlCapacity.RetainedRecords);
        Assert.Equal(before.RetainedRecords, after.RetainedRecords);
        Assert.Equal(before.RetainedPayloadBytes, after.RetainedPayloadBytes);
        await AssertUnchangedAsync(app, context, outbox, business, facts);
        Assert.Equal(retried.Value, (await policies.AdjustAsync(request, "42", Now.AddDays(1), null)).Value);
        Assert.Equal(after, (await policies.ReadPolicyAsync()).Value);
        Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == retried.Value.EventId);
    }

    private static async Task VerifyLoweringAsync(WebApplication app, string context)
    {
        Assert.True((await WriteAsync(app, context, 1)).IsSuccess);
        Assert.True((await WriteAsync(app, context, 2)).IsSuccess);
        var business = await ReadBusinessAsync(app, context);
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
        var outbox = OwnedOutbox(scope.ServiceProvider, context);
        var before = (await policies.ReadPolicyAsync()).Value;
        var facts = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        var initialRecords = context == "files" ? 4 : 2;
        Assert.Equal(initialRecords, before.RetainedRecords);
        Assert.True(before.RetainedPayloadBytes > 1);
        Assert.Equal(initialRecords, facts.Count);

        var lowerRecords = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 1, before.MaxPayloadBytes,
            before.MaxRecordPayloadBytes, "operator-adjustment");
        var recordsReceipt = (await policies.AdjustAsync(lowerRecords, "42", Now, null)).Value;
        Assert.Equal(2, recordsReceipt.PolicyRevision);
        var records = (await policies.ReadPolicyAsync()).Value;
        Assert.True(records.OverLimit);
        Assert.Equal(0, records.RemainingRecords);
        Assert.True(records.RemainingPayloadBytes > 0);
        Assert.Equal(before.RetainedRecords, records.RetainedRecords);
        Assert.Equal(before.RetainedPayloadBytes, records.RetainedPayloadBytes);
        Assert.Equal(CapacityError(context), (await WriteAsync(app, context, 3)).Error.Code);
        await AssertUnchangedAsync(app, context, outbox, business, facts);

        var lowerBytes = lowerRecords with
        {
            RequestId = Guid.NewGuid(),
            ExpectedPolicyRevision = 2,
            MaxRecords = before.MaxRecords,
            MaxPayloadBytes = 1,
            MaxRecordPayloadBytes = 1,
        };
        var bytesReceipt = (await policies.AdjustAsync(lowerBytes, "42", Now.AddMinutes(1), null)).Value;
        Assert.Equal(3, bytesReceipt.PolicyRevision);
        var bytes = (await policies.ReadPolicyAsync()).Value;
        Assert.True(bytes.OverLimit);
        Assert.Equal(0, bytes.RemainingPayloadBytes);
        Assert.True(bytes.RemainingRecords > 0);
        Assert.Equal(before.RetainedRecords, bytes.RetainedRecords);
        Assert.Equal(before.RetainedPayloadBytes, bytes.RetainedPayloadBytes);
        Assert.Equal(CapacityError(context), (await WriteAsync(app, context, 3)).Error.Code);
        await AssertUnchangedAsync(app, context, outbox, business, facts);

        var restore = lowerBytes with
        {
            RequestId = Guid.NewGuid(),
            ExpectedPolicyRevision = 3,
            MaxPayloadBytes = before.MaxPayloadBytes,
            MaxRecordPayloadBytes = before.MaxRecordPayloadBytes,
        };
        var restored = (await policies.AdjustAsync(restore, "42", Now.AddMinutes(2), null)).Value;
        Assert.Equal(4, restored.PolicyRevision);
        Assert.False((await policies.ReadPolicyAsync()).Value.OverLimit);
        await AssertUnchangedAsync(app, context, outbox, business, facts);
        var changes = (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue))
            .Where(entry => entry.EventName == context + ".fact-capacity-policy-changed.v1").ToArray();
        Assert.Equal(3, changes.Length);
        foreach (var receipt in new[] { recordsReceipt, bytesReceipt, restored })
        {
            using var evidence = JsonDocument.Parse(Assert.Single(changes, entry => entry.Id == receipt.EventId).Payload);
            var root = evidence.RootElement;
            Assert.Equal(receipt.RequestId, root.GetProperty("requestId").GetGuid());
            Assert.Equal(receipt.PolicyRevision, root.GetProperty("policyRevision").GetInt64());
            Assert.Equal("42", root.GetProperty("actorId").GetString());
            Assert.Equal(receipt.Previous, root.GetProperty("previous").Deserialize<FactCapacityPolicyLimits>(JsonOptions));
            Assert.Equal(receipt.Current, root.GetProperty("current").Deserialize<FactCapacityPolicyLimits>(JsonOptions));
        }
        Assert.True((await WriteAsync(app, context, 3)).IsSuccess);
        var recoveredRecords = context == "files" ? 6 : 3;
        Assert.Equal(recoveredRecords, (await policies.ReadPolicyAsync()).Value.RetainedRecords);
        Assert.Equal(business, await ReadBusinessAsync(app, context));
        await using var read = app.Services.CreateAsyncScope();
        Assert.NotNull(await ReadItemAsync(read.ServiceProvider, context, 3));
        Assert.Equal(recoveredRecords, (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue))
            .Count(entry => entry.EventName != context + ".fact-capacity-policy-changed.v1"));
    }

    private static string CapacityError(string context) => context switch
    {
        "platform" => "platform.audit_capacity.exhausted",
        "identity" => "identity.audit_capacity.exhausted",
        "files" => "files.audit_capacity.exhausted",
        "scheduling" => "scheduling.audit_capacity_exhausted",
        "costing" => "costing.audit_capacity_exhausted",
        "pricing" => "pricing.audit_capacity_exhausted",
        _ => throw new ArgumentException("Unknown owned module.", nameof(context)),
    };

    private static WebApplication CreateApp(string context, string? connectionString = null, IIntegrationEventSerializer? serializer = null)
    {
        var configurationContext = context switch
        {
            "platform" => "Platform",
            "identity" => "Identity",
            "files" => "Files",
            "scheduling" => "Scheduling",
            "costing" => "Costing",
            "pricing" => "Pricing",
            _ => throw new ArgumentException("Unknown owned module.", nameof(context)),
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "policy-isolation-test-signing-key-long-enough-for-hs256",
            [$"{configurationContext}:Storage:Provider"] = connectionString is null ? "Memory" : "Postgres",
            [$"ConnectionStrings:{configurationContext}"] = connectionString,
            [$"{configurationContext}:Messaging:Enabled"] = "false",
            [$"{configurationContext}:Worker:Enabled"] = "false",
            [$"{configurationContext}:AuditDelivery:Cleanup:Enabled"] = "false",
            [$"{configurationContext}:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["OperationJournal:Storage:Provider"] = "Memory",
            ["OperationJournal:Cleanup:Enabled"] = "false",
            ["Files:StorageRoot"] = Path.Combine(Path.GetTempPath(), "nsn-policy-isolation-" + Guid.NewGuid().ToString("N")),
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 21 });
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("42"));
        builder.Services.AddSingleton<IClock>(new FixedClock(Now));
        if (serializer is not null) { builder.Services.AddSingleton(serializer); }
        if (context is "costing" or "pricing")
        { builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, context); }
        switch (context)
        {
            case "platform": builder.Services.AddPlatformModule(builder.Configuration, builder.Environment); break;
            case "identity": builder.Services.AddIdentityModule(builder.Configuration, builder.Environment); break;
            case "files": builder.Services.AddFilesModule(builder.Configuration, builder.Environment); break;
            case "scheduling": builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment); break;
            case "costing": builder.Services.AddCostingModule(builder.Configuration); break;
            case "pricing": builder.Services.AddPricingModule(builder.Configuration); break;
        }
        return builder.Build();
    }

    private static async Task<Result> WriteAsync(WebApplication app, string context, int index)
    {
        await using var scope = app.Services.CreateAsyncScope();
        try
        {
            var services = scope.ServiceProvider;
            switch (context)
            {
                case "platform":
                    return await services.GetRequiredService<SettingStore>()
                    .WriteAsync(SettingKey.Create("policy.item-" + index).Value, "kept-value");
                case "identity":
                    await services.GetRequiredService<IRoleRepository>().AddAsync(Role.Create(new RoleId(1000 + index),
                        RoleCode.Create("policy-role-" + index).Value, RoleName.Create("Kept role " + index).Value));
                    await services.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
                    return Result.Success();
                case "files":
                    var file = StoredFile.Register(new StoredFileId(1000 + index), FileName.Create("kept-file.bin").Value,
                        "application/octet-stream", "42", Now).Value;
                    Assert.True(file.MarkStored("policy-storage-" + index, 3).IsSuccess);
                    await services.GetRequiredService<IStoredFileRepository>().SaveAsync(file);
                    return Result.Success();
                case "scheduling":
                    var plan = ScheduledTask.Create(new ScheduledTaskId(1000 + index), TaskCode.Create("policy-plan-" + index).Value,
                        TimeSpan.FromHours(1), Now.AddDays(1), ScheduleTarget.Create("costing.recalculate", ItemIds[index - 1]).Value, "42").Value;
                    return await services.GetRequiredService<IScheduledTaskStore>().AddAsync(plan);
                case "costing":
                    var cost = await services.GetRequiredService<ISender>().SendAsync(new UpdateCostInputs(TaskIds[index - 1], ItemIds[index - 1], 0, 80m, 20m));
                    return cost.IsSuccess ? Result.Success() : Result.Failure(cost.Error);
                case "pricing":
                    var price = await services.GetRequiredService<ISender>().SendAsync(new UpdatePricingCost(TaskIds[index - 1], ItemIds[index - 1], 0, 80m, 0.2m));
                    return price.IsSuccess ? Result.Success() : Result.Failure(price.Error);
                default: throw new ArgumentException("Unknown owned module.", nameof(context));
            }
        }
        catch (IdentityAuditCapacityException error) { return Result.Failure(error.Reason); }
        catch (FileAuditCapacityException error) { return Result.Failure(error.Reason); }
    }

    private static async Task<object?> ReadItemAsync(IServiceProvider services, string context, int index)
    {
        switch (context)
        {
            case "platform": return await services.GetRequiredService<SettingStore>().GetAsync(SettingKey.Create("policy.item-" + index).Value);
            case "identity": return await services.GetRequiredService<IRoleRepository>().FindByCodeAsync(RoleCode.Create("policy-role-" + index).Value);
            case "files": return await services.GetRequiredService<IStoredFileRepository>().FindAsync(new StoredFileId(1000 + index));
            case "scheduling": return await services.GetRequiredService<IScheduledTaskStore>().FindAsync(new ScheduledTaskId(1000 + index));
            case "costing":
                var cost = await services.GetRequiredService<ISender>().QueryAsync(new GetCostSheet(ItemIds[index - 1]));
                var calculation = await services.GetRequiredService<ISender>().QueryAsync(new GetCostCalculation(TaskIds[index - 1]));
                if (cost.IsFailure)
                {
                    Assert.Equal("costing.not_found", cost.Error.Code);
                    Assert.Equal("costing.not_found", calculation.Error.Code);
                    return null;
                }
                Assert.True(calculation.IsSuccess);
                return new { Sheet = cost.Value, Task = calculation.Value };
            case "pricing":
                var price = await services.GetRequiredService<ISender>().QueryAsync(new GetPriceQuote(ItemIds[index - 1]));
                var recalculation = await services.GetRequiredService<ISender>().QueryAsync(new GetRecalculation(TaskIds[index - 1]));
                if (price.IsFailure)
                {
                    Assert.Equal("pricing.not_found", price.Error.Code);
                    Assert.Equal("pricing.not_found", recalculation.Error.Code);
                    return null;
                }
                Assert.True(recalculation.IsSuccess);
                return new { Quote = price.Value, Task = recalculation.Value };
            default: throw new ArgumentException("Unknown owned module.", nameof(context));
        }
    }

    private static IOutboxStore OwnedOutbox(IServiceProvider services, string context)
        => context is "costing" or "pricing" ? services.GetRequiredService<IOutboxStore>()
            : services.GetRequiredKeyedService<IOutboxStore>(context);

    private static async Task<string> ReadBusinessAsync(WebApplication app, string context)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var first = await ReadItemAsync(scope.ServiceProvider, context, 1);
        var second = await ReadItemAsync(scope.ServiceProvider, context, 2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        return JsonSerializer.Serialize(new[] { first, second });
    }

    private static async Task AssertUnchangedAsync(WebApplication app, string context, IOutboxStore outbox, string business, IReadOnlyList<OutboxEntry> facts)
    {
        Assert.Equal(business, await ReadBusinessAsync(app, context));
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Null(await ReadItemAsync(scope.ServiceProvider, context, 3));
        Assert.Equal(facts, (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue))
            .Where(entry => entry.EventName != context + ".fact-capacity-policy-changed.v1"));
    }
}
