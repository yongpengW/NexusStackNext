using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Files.Application;
using NexusStackNext.Identity.Application;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.Scheduling.Application;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactRecoveryProtocolTests
{
    [Fact]
    public async Task PlatformMemory_CompetingRecoveriesHaveOneWinner_AndReplayPreservesOriginalDecision()
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        Assert.True((await settings.WriteAsync(SettingKey.Create("recovery.protocol").Value, "original")).IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        await VerifyAsync(app.Services, "platform", original);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("files")]
    [InlineData("scheduling")]
    public async Task PlatformHostMemory_CompetingRecoveriesHaveOneWinner_AndReplayPreservesOriginalDecision(string source)
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyPolicyAsync(client, app.Services, source);
    }

    [PostgresFact]
    public Task PlatformPostgres_CompetingRecoveriesHaveOneWinner_AndReplayPreservesOriginalDecision()
        => VerifyPlatformPostgresAsync("platform");

    [PostgresFact]
    public Task IdentityPostgres_CompetingRecoveriesHaveOneWinner_AndReplayPreservesOriginalDecision()
        => VerifyPlatformPostgresAsync("identity");

    [PostgresFact]
    public Task FilesPostgres_CompetingRecoveriesHaveOneWinner_AndReplayPreservesOriginalDecision()
        => VerifyPlatformPostgresAsync("files");

    [PostgresFact]
    public Task SchedulingPostgres_CompetingRecoveriesHaveOneWinner_AndReplayPreservesOriginalDecision()
        => VerifyPlatformPostgresAsync("scheduling");

    [PostgresFact]
    public Task CostingPostgres_CompetingRecoveriesHaveOneWinner_AndReplayPreservesOriginalDecision()
        => VerifyBusinessPostgresAsync("costing");

    [PostgresFact]
    public Task PricingPostgres_CompetingRecoveriesHaveOneWinner_AndReplayPreservesOriginalDecision()
        => VerifyBusinessPostgresAsync("pricing");

    private static async Task VerifyPlatformPostgresAsync(string source)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, PlatformAppWithRootAccount.RootPassword,
            schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", PlatformAppWithRootAccount.RootPassword);
        await VerifyPolicyAsync(client, app.Services, source);
    }

    private static async Task VerifyBusinessPostgresAsync(string source)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        if (source == "costing") { await CostingDatabase.MigrateAsync(database.ConnectionString); }
        else { await PricingDatabase.MigrateAsync(database.ConnectionString); }
        var assembly = source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
        await using var host = await BusinessProcess.StartAsync(assembly, source == "costing" ? "Costing" : "Pricing", database.ConnectionString);
        host.Authenticate();
        await using var reader = source == "costing" ? TaskOperationTests.CreateCostingApp(database.ConnectionString, null)
            : TaskOperationTests.CreatePricingApp(database.ConnectionString, null);
        await VerifyPolicyAsync(host.Client, reader.Services, source);
    }

    private static async Task VerifyPolicyAsync(HttpClient client, IServiceProvider services, string source)
    {
        var path = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        using var initial = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var policy = await initial.Content.ReadApiDataAsync();
        using var adjusted = await client.PutAsJsonAsync(path, new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = policy.GetProperty("policyRevision").GetString(),
            maxRecords = policy.GetProperty("maxRecords").GetString(),
            maxPayloadBytes = policy.GetProperty("maxPayloadBytes").GetString(),
            maxRecordPayloadBytes = policy.GetProperty("maxRecordPayloadBytes").GetInt32() + 1,
            reason = "operator-adjustment",
        });
        Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
        var messageId = (await adjusted.Content.ReadApiDataAsync()).GetProperty("eventId").GetGuid();
        await using var scope = services.CreateAsyncScope();
        var outbox = GetOutbox(scope.ServiceProvider, source);
        var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        using var beforeRecovery = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, beforeRecovery.StatusCode);
        var beforePolicy = await beforeRecovery.Content.ReadApiDataAsync();
        await VerifyAsync(services, source, original);
        using var after = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var current = await after.Content.ReadApiDataAsync();
        Assert.True(JsonElement.DeepEquals(beforePolicy, current));
    }

    private static async Task VerifyAsync(IServiceProvider services, string source, OutboxEntry original)
    {
        await using var scope = services.CreateAsyncScope();
        var outbox = GetOutbox(scope.ServiceProvider, source);
        var stoppedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-stop", stoppedAt, 0));
        var delivery = GetPort(scope.ServiceProvider, source);
        var before = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(0, before.Capacity.RetainedRecords);

        var requests = new[]
        {
            new FactDeliveryRecoveryRequest(Guid.Parse("10300000-0000-0000-0000-000000000001"), original.Id, stoppedAt, 0, "manual-retry"),
            new FactDeliveryRecoveryRequest(Guid.Parse("10300000-0000-0000-0000-000000000002"), original.Id, stoppedAt, 0, "dependency-restored"),
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        var arrived = 0;
        var contenders = requests.Select(request => Task.Run(async () =>
        {
            await using var ownScope = services.CreateAsyncScope();
            var ownDelivery = GetPort(ownScope.ServiceProvider, source);
            if (Interlocked.Increment(ref arrived) == 2) { ready.SetResult(); }
            await start.Task.WaitAsync(caller.Token);
            return await ownDelivery.RecoverAsync(request, "original-operator", stoppedAt, null, caller.Token);
        })).ToArray();
        Result<FactDeliveryRecoveryReceipt>[] decisions;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            start.SetResult();
            decisions = await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            caller.Cancel();
            start.TrySetResult();
            try { await Task.WhenAll(contenders); }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
        }
        var accepted = Assert.Single(decisions, decision => decision.IsSuccess).Value;
        Assert.Equal($"{source}.delivery_conflict", Assert.Single(decisions, decision => decision.IsFailure).Error.Code);
        var winner = Assert.Single(requests, request => request.RequestId == accepted.RequestId);
        var loser = Assert.Single(requests, request => request.RequestId != accepted.RequestId);
        Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(loser.RequestId)).Error.Code);
        Assert.Equal(accepted, (await delivery.GetRecoveryAsync(winner.RequestId)).Value);
        Assert.Equal(1, accepted.RetryRevision);
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == original.Id);
        Assert.Equal(original.Id, pending.Id);
        Assert.Equal(original.EventName, pending.EventName);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, pending.RetryRevision);
        var once = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(1, once.Capacity.RetainedRecords);
        Assert.True(once.Capacity.RetainedPayloadBytes > 0);

        Assert.Equal($"{source}.delivery_recovery.request_conflict",
            (await delivery.RecoverAsync(winner, "different-operator", stoppedAt, null)).Error.Code);
        foreach (var changed in new[]
        {
            winner with { MessageId = Guid.NewGuid() },
            winner with { ExpectedDeadLetteredAt = stoppedAt.AddSeconds(1) },
            winner with { ExpectedRetryRevision = 1 },
            winner with { Reason = winner.Reason == "manual-retry" ? "dependency-restored" : "manual-retry" },
        })
        {
            Assert.Equal($"{source}.delivery_recovery.request_conflict",
                (await delivery.RecoverAsync(changed, "original-operator", stoppedAt, null)).Error.Code);
        }
        Assert.Equal(once, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(accepted, (await delivery.RecoverAsync(winner, "original-operator", stoppedAt.AddDays(1), null)).Value);
        Assert.False(await outbox.MarkFailedAsync(original.Id, "late-old-failure", stoppedAt, 0));
        Assert.False(await outbox.MarkDeadLetteredAsync(original.Id, "late-old-stop", stoppedAt, 0));
        Assert.Equal(pending, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == original.Id));

        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "same-time-next-stop", stoppedAt, 1));
        Assert.Equal(accepted, (await delivery.RecoverAsync(winner, "original-operator", stoppedAt, null)).Value);
        Assert.DoesNotContain(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == original.Id);
        Assert.Equal($"{source}.delivery_conflict",
            (await delivery.RecoverAsync(loser, "original-operator", stoppedAt, null)).Error.Code);
        var secondRequest = loser with { ExpectedRetryRevision = 1 };
        var second = await delivery.RecoverAsync(secondRequest, "original-operator", stoppedAt, null);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, second.Value.RetryRevision);
        Assert.Equal(2, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);

        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "same-time-third-stop", stoppedAt, 2));
        Assert.Equal(0, await delivery.CleanupRecoveriesAsync(10, stoppedAt.AddDays(7).AddTicks(-1)));
        Assert.Equal(2, await delivery.CleanupRecoveriesAsync(10, stoppedAt.AddDays(7)));
        var empty = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
        Assert.Equal(0, empty.RetainedRecords);
        Assert.Equal(0, empty.RetainedPayloadBytes);
        Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(winner.RequestId)).Error.Code);
        var reused = winner with { ExpectedRetryRevision = 2 };
        var newDecision = await delivery.RecoverAsync(reused, "different-operator", stoppedAt.AddDays(7), null);
        Assert.True(newDecision.IsSuccess);
        Assert.Equal(3, newDecision.Value.RetryRevision);
        Assert.Equal("different-operator", newDecision.Value.ActorId);
        Assert.NotEqual(accepted, newDecision.Value);
        Assert.Equal(1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);

        await outbox.MarkDeliveredAsync(original.Id, stoppedAt.AddDays(7));
        Assert.False(await outbox.MarkFailedAsync(original.Id, "late-current-failure", stoppedAt, 3));
        Assert.False(await outbox.MarkDeadLetteredAsync(original.Id, "late-current-stop", stoppedAt, 3));
        Assert.Equal("Delivered", (await delivery.GetAsync(original.Id)).Value.State);
        Assert.Equal(newDecision.Value,
            (await delivery.RecoverAsync(reused, "different-operator", stoppedAt.AddDays(8), null)).Value);
        Assert.Equal($"{source}.delivery_conflict", (await delivery.RecoverAsync(
            reused with { RequestId = Guid.NewGuid(), ExpectedRetryRevision = 3 }, "different-operator", stoppedAt, null)).Error.Code);
    }

    internal static IOutboxStore GetOutbox(IServiceProvider services, string source)
        => source is "costing" or "pricing" ? services.GetRequiredService<IOutboxStore>()
            : services.GetRequiredKeyedService<IOutboxStore>(source);

    internal static RecoveryPort GetPort(IServiceProvider services, string source)
    {
        object port = source switch
        {
            "platform" => services.GetRequiredService<ISettingAuditDelivery>(),
            "identity" => services.GetRequiredService<IIdentityAuditDelivery>(),
            "files" => services.GetRequiredService<IFileAuditDelivery>(),
            "scheduling" => services.GetRequiredService<ISchedulingAuditDelivery>(),
            "costing" => services.GetRequiredService<ICostingAuditDelivery>(),
            "pricing" => services.GetRequiredService<IPricingAuditDelivery>(),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        return port switch
        {
            ISettingAuditDelivery value => new(value.RecoverAsync, value.GetAsync, value.GetRecoveryAsync, value.ReadRecoveryCapacityAsync, value.CleanupRecoveriesAsync),
            IIdentityAuditDelivery value => new(value.RecoverAsync, value.GetAsync, value.GetRecoveryAsync, value.ReadRecoveryCapacityAsync, value.CleanupRecoveriesAsync),
            IFileAuditDelivery value => new(value.RecoverAsync, value.GetAsync, value.GetRecoveryAsync, value.ReadRecoveryCapacityAsync, value.CleanupRecoveriesAsync),
            ISchedulingAuditDelivery value => new(value.RecoverAsync, value.GetAsync, value.GetRecoveryAsync, value.ReadRecoveryCapacityAsync, value.CleanupRecoveriesAsync),
            ICostingAuditDelivery value => new(value.RecoverAsync, value.GetAsync, value.GetRecoveryAsync, value.ReadRecoveryCapacityAsync, value.CleanupRecoveriesAsync),
            IPricingAuditDelivery value => new(value.RecoverAsync, value.GetAsync, value.GetRecoveryAsync, value.ReadRecoveryCapacityAsync, value.CleanupRecoveriesAsync),
            _ => throw new InvalidOperationException("Unknown recovery port."),
        };
    }

    internal sealed record RecoveryPort(
        Func<FactDeliveryRecoveryRequest, string, DateTimeOffset, ExecutionOrigin?, CancellationToken, Task<Result<FactDeliveryRecoveryReceipt>>> Recover,
        Func<Guid, CancellationToken, Task<Result<FactDeliveryState>>> Get,
        Func<Guid, CancellationToken, Task<Result<FactDeliveryRecoveryReceipt>>> Receipt,
        Func<CancellationToken, Task<Result<FactDeliveryRecoveryCapacity>>> Capacity,
        Func<int, DateTimeOffset, CancellationToken, Task<int>> Cleanup)
    {
        public Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actor,
            DateTimeOffset now, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
            => Recover(request, actor, now, execution, cancellationToken);
        public Task<Result<FactDeliveryState>> GetAsync(Guid messageId) => Get(messageId, CancellationToken.None);
        public Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId) => Receipt(requestId, CancellationToken.None);
        public Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync() => Capacity(CancellationToken.None);
        public Task<int> CleanupRecoveriesAsync(int batch, DateTimeOffset now, CancellationToken cancellationToken = default)
            => Cleanup(batch, now, cancellationToken);
    }
}
