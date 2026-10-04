using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Identity.Application;
using NexusStackNext.TestSupport;
using BudgetApp = NexusStackNext.HostIntegration.Tests.MemoryFactWriteBudgetTests.BudgetApp;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryIdentityQueryFailureTests
{
    [Theory]
    [InlineData("busy")]
    [InlineData("unknown")]
    [InlineData("cancel")]
    public async Task QueryAdapter_OnlyTranslatesKnownBusy_AndPreservesOtherFailures(string failure)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        FaultingPermissionCache? cache = null;
        await using var baseApp = new BudgetApp(clock, "Identity") { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IPermissionCache>(provider => cache = new FaultingPermissionCache(
                new UserPermissionCache(provider.GetRequiredService<IPermissionSource>())))));
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var user = await sender.SendAsync(new CreateUserCommand("query-failure", "query-failure-password"));
        Assert.True(user.IsSuccess);
        Assert.NotNull(cache);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var capacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("identity");
        var before = await outbox.ReadPendingAsync(10, clock.UtcNow);
        var beforeCapacity = (await capacity.ReadAsync()).Value;
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") { cancellation.Cancel(); }
        Exception error = failure switch
        {
            "busy" => new CommittedFactCapacityBusyException(),
            "cancel" => new OperationCanceledException(cancellation.Token),
            _ => new InvalidOperationException("Injected permission cache failure."),
        };
        cache.ReadFailure = error;
        var query = new GetUserPermissionsQuery(user.Value);
        if (failure == "busy") { Assert.Equal("audit_capacity.busy", (await sender.QueryAsync(query)).Error.Code); }
        else if (failure == "cancel")
        {
            var propagated = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.QueryAsync(query, cancellation.Token));
            Assert.Same(error, propagated);
        }
        else
        {
            var propagated = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.QueryAsync(query));
            Assert.Same(error, propagated);
        }
        cache.ReadFailure = null;
        var recovered = await sender.QueryAsync(query);
        Assert.True(recovered.IsSuccess);
        Assert.Empty(recovered.Value);
        Assert.Equal(before, await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.Equal(beforeCapacity, (await capacity.ReadAsync()).Value);
    }
}
