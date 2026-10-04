using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryPlatformFactCapacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactConstructionFailure_PreservesStateAndCapacity_AndSameStoreCanRetry(bool updating)
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var serializer = new RejectingEventSerializer(new SystemTextJsonIntegrationEventSerializer());
        var repository = new InMemorySettingRepository(serializer, new() { MaxRecords = 2 });
        var store = new SettingStore(repository, new SequentialIdGenerator(1000), clock, new AnonymousCurrentUser());
        var key = SettingKey.Create("capacity.failure").Value;
        if (updating) { Assert.True((await store.WriteAsync(key, "before")).IsSuccess); }
        var before = await store.GetAsync(key);
        var original = await repository.ReadPendingAsync(10, clock.UtcNow);
        serializer.ShouldReject = _ => true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(key, "not-committed"));
        var unchanged = await store.GetAsync(key);
        Assert.Equal(before?.Value, unchanged?.Value);
        Assert.Equal(before?.Version, unchanged?.Version);
        Assert.Equal(original, await repository.ReadPendingAsync(10, clock.UtcNow));
        serializer.ShouldReject = null;
        Assert.True((await store.WriteAsync(key, "recovered")).IsSuccess);
        if (!updating) { Assert.True((await store.WriteAsync(SettingKey.Create("capacity.remaining").Value, "last-slot")).IsSuccess); }
        Assert.Equal(2, (await repository.ReadPendingAsync(10, clock.UtcNow)).Count);
        Assert.Equal(SettingStore.AuditCapacityExhausted, (await store.WriteAsync(SettingKey.Create("capacity.excess").Value, "excess")).Error);
    }

    [Fact]
    public async Task Utf8Limits_RejectOversizedFactWithoutUsingBudget_AndKeepTwoExactThreeByteFacts()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var serializer = new KnownPayloadSerializer(new SystemTextJsonIntegrationEventSerializer()) { Payload = "中文" };
        var repository = new InMemorySettingRepository(serializer, new() { MaxRecords = 10, MaxPayloadBytes = 6, MaxRecordPayloadBytes = 3 });
        var store = new SettingStore(repository, new SequentialIdGenerator(1000), clock, new AnonymousCurrentUser());
        var first = SettingKey.Create("capacity.utf8first").Value;
        Assert.Equal(SettingStore.AuditCapacityExhausted, (await store.WriteAsync(first, "not-committed")).Error);
        Assert.Null(await store.GetAsync(first));
        Assert.Empty(await repository.ReadPendingAsync(10, clock.UtcNow));

        // Each of these known Unicode characters occupies three UTF-8 bytes; no production sizing helper supplies the expected budget.
        serializer.Payload = "中";
        Assert.True((await store.WriteAsync(first, "first")).IsSuccess);
        serializer.Payload = "文";
        var second = SettingKey.Create("capacity.utf8second").Value;
        Assert.True((await store.WriteAsync(second, "second")).IsSuccess);
        serializer.Payload = "a";
        var third = SettingKey.Create("capacity.utf8third").Value;
        Assert.Equal(SettingStore.AuditCapacityExhausted, (await store.WriteAsync(third, "not-committed")).Error);
        Assert.Null(await store.GetAsync(third));
        Assert.Equal(new[] { "中", "文" }, (await repository.ReadPendingAsync(10, clock.UtcNow)).Select(entry => entry.Payload).Order(StringComparer.Ordinal));
        Assert.Equal("first", (await store.GetAsync(first))?.Value);
        Assert.Equal("second", (await store.GetAsync(second))?.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringFactConstruction_PublishesNeitherBusinessNorCapacity_AndCanRetry(bool updating)
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var serializer = new CancelingEventSerializer(new SystemTextJsonIntegrationEventSerializer());
        var repository = new InMemorySettingRepository(serializer, new() { MaxRecords = 2 });
        var store = new SettingStore(repository, new SequentialIdGenerator(1000), clock, new AnonymousCurrentUser());
        var key = SettingKey.Create("capacity.cancel").Value;
        if (updating) { Assert.True((await store.WriteAsync(key, "before")).IsSuccess); }
        using var cancellation = new CancellationTokenSource();
        serializer.CancelOnSerialize = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(key, "must-not-commit", cancellationToken: cancellation.Token));
        serializer.CancelOnSerialize = null;
        var retained = await store.GetAsync(key);
        if (updating)
        {
            Assert.NotNull(retained);
            Assert.Equal("before", retained.Value);
            Assert.Equal(1, retained.Version);
        }
        else { Assert.Null(retained); }
        Assert.Equal(updating ? 1 : 0, (await repository.ReadPendingAsync(10, clock.UtcNow)).Count);

        Assert.True((await store.WriteAsync(key, "recovered")).IsSuccess);
        var recovered = await store.GetAsync(key);
        Assert.NotNull(recovered);
        Assert.Equal("recovered", recovered.Value);
        Assert.Equal(updating ? 2 : 1, recovered.Version);
        if (!updating) { Assert.True((await store.WriteAsync(SettingKey.Create("capacity.remaining").Value, "last-slot")).IsSuccess); }
        Assert.Equal(2, (await repository.ReadPendingAsync(10, clock.UtcNow)).Count);
        Assert.Equal(SettingStore.AuditCapacityExhausted, (await store.WriteAsync(SettingKey.Create("capacity.extra").Value, "over-limit")).Error);
    }

    [Fact]
    public async Task FullCapacity_RejectsChangesTogether_AndSameScopeRecoversOnlyAfterConfirmedCopiesExpire()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var app = new CapacityApp(clock) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("platform");
        var key = SettingKey.Create("capacity.first").Value;
        Assert.True((await store.WriteAsync(key, "original", expectedVersion: 0)).IsSuccess);
        var first = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.True((await store.WriteAsync(key, "original", expectedVersion: 1)).IsSuccess);
        Assert.Equal(first, Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)));

        var refused = await store.WriteAsync(key, "must-rollback", expectedVersion: 1);
        Assert.True(refused.IsFailure);
        Assert.Equal(SettingStore.AuditCapacityExhausted, refused.Error);
        var other = SettingKey.Create("capacity.second").Value;
        Assert.Equal(SettingStore.AuditCapacityExhausted, (await store.WriteAsync(other, "never", expectedVersion: 0)).Error);
        Assert.Null(await store.GetAsync(other));
        var retained = await store.GetAsync(key);
        Assert.NotNull(retained);
        Assert.Equal("original", retained.Value);
        Assert.Equal(1, retained.Version);
        Assert.Equal(first, Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)));

        await outbox.MarkDeliveredAsync(first.Id, clock.UtcNow);
        Assert.Equal(0, await cleanup.CleanupAsync());
        Assert.Equal(SettingStore.AuditCapacityExhausted, (await store.WriteAsync(key, "still-full", expectedVersion: 1)).Error);
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(1, await cleanup.CleanupAsync());
        Assert.Equal(0, await cleanup.CleanupAsync());
        Assert.True((await store.WriteAsync(key, "recovered", expectedVersion: 1)).IsSuccess);
        var recovered = await store.GetAsync(key);
        Assert.NotNull(recovered);
        Assert.Equal("recovered", recovered.Value);
        Assert.Equal(2, recovered.Version);
        Assert.NotEqual(first.Id, Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)).Id);
    }

    private sealed class CapacityApp(MutableClock clock) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:AuditDelivery:MemoryCapacity:MaxRecords"] = "1",
                ["Platform:AuditDelivery:Cleanup:Enabled"] = "false",
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock));
        }
    }

}
