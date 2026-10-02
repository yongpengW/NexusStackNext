using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CommandOperationTests
{
    [Theory]
    [InlineData("completed", false)]
    [InlineData("rejected", false)]
    [InlineData("failed", false)]
    [InlineData("canceled", false)]
    [InlineData("completed", true)]
    [InlineData("rejected", true)]
    [InlineData("failed", true)]
    [InlineData("canceled", true)]
    public async Task CommandOutcome_IsPreserved_EvenWhenJournalWritesFail(string outcome, bool journalUnavailable)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OperationJournal:Storage:Provider"] = "Memory" });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "command-probe");
        builder.Services.AddCommandHandler<OutcomeCommand, int, OutcomeHandler>();
        builder.Services.AddRequestValidator<OutcomeCommand, OutcomeValidator>();
        if (journalUnavailable) { builder.Services.AddSingleton<IOperationJournal, UnavailableJournal>(); }
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var cancellation = new CancellationTokenSource();
        if (outcome == "canceled") { await cancellation.CancelAsync(); }
        if (outcome == "failed")
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new OutcomeCommand(outcome)));
            Assert.Equal("private-command-failure", failure.Message);
        }
        else if (outcome == "canceled")
        {
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(new OutcomeCommand(outcome), cancellation.Token));
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
        }
        else
        {
            var result = await sender.SendAsync(new OutcomeCommand(outcome));
            if (outcome == "completed") { Assert.Equal(21, result.Value); }
            else { Assert.Equal("probe.validation", result.Error.Code); }
        }
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var phases = await OperationEndpointInventoryTests.ReadAsync(journal);
        if (journalUnavailable)
        {
            Assert.Empty(phases);
            Assert.Equal(2, app.Services.GetRequiredService<OperationJournalStatus>().FailureCount);
        }
        else
        {
            Assert.Equal(2, phases.Length);
            var finished = Assert.Single(phases, phase => phase.Phase == "finished");
            Assert.Equal(outcome, finished.Outcome);
            Assert.All(phases, phase => Assert.DoesNotContain("private-", System.Text.Json.JsonSerializer.Serialize(phase), StringComparison.Ordinal));
        }
        // 上次取消或异常不能残留操作作用域，也不能影响下一次成功调用。
        Assert.Equal(21, (await sender.SendAsync(new OutcomeCommand("completed"))).Value);
        if (!journalUnavailable)
        {
            var all = await OperationEndpointInventoryTests.ReadAsync(journal);
            Assert.Equal(4, all.Length);
            Assert.Equal(2, all.Select(item => item.OperationId).Distinct().Count());
        }
    }

    [Fact]
    public async Task ConcurrentCommands_KeepIndependentOperationsAndDeclaredTaskIds()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OperationJournal:Storage:Provider"] = "Memory" });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "command-probe");
        builder.Services.AddSingleton<ConcurrentGate>();
        builder.Services.AddCommandHandler<ConcurrentCommand, Guid, ConcurrentHandler>();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var tasks = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var results = await Task.WhenAll(tasks.Select(task => sender.SendAsync(new ConcurrentCommand(task))));
        Assert.Equal(tasks, results.Select(result => result.Value));
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var phases = await OperationEndpointInventoryTests.ReadAsync(journal);
        Assert.Equal(4, phases.Length);
        Assert.Equal(2, phases.Select(item => item.OperationId).Distinct().Count());
        foreach (var task in tasks)
        {
            var operation = phases.Where(item => item.Metadata!.TaskId == task).ToArray();
            Assert.Equal(2, operation.Length);
            Assert.Single(operation.Select(item => item.OperationId).Distinct());
            Assert.Equal("completed", Assert.Single(operation, item => item.Phase == "finished").Outcome);
            Assert.All(operation, item => Assert.Null(item.Metadata!.TaskEpoch));
        }
    }

    [Fact]
    public async Task CommandAfterItsParentFinished_DoesNotReuseTheFinishedScope()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "command-probe");
        builder.Services.AddCommandHandler<ProbeCommand, int, ProbeHandler>();
        builder.Services.AddCommandHandler<LaunchCommand, Task<int>, LaunchHandler>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddSingleton(release);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();

        var launched = await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new LaunchCommand());
        release.SetResult();
        Assert.Equal(21, await launched.Value.WaitAsync(TimeSpan.FromSeconds(5)));

        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var messages = await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        Assert.Equal(4, messages.Count);
        var observations = messages.Select(message => new SystemTextJsonIntegrationEventSerializer()
            .Deserialize<OperationObservedV1>(message.Payload)).ToArray();
        Assert.Equal(2, observations.Select(item => item.OperationId).Distinct().Count());
        Assert.Equal(2, observations.Count(item => item.Phase == "finished"));
    }

    [Fact]
    public async Task SuppressedHttpEndpoint_DoesNotReappearAsACommandOperation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "command-probe");
        builder.Services.AddCommandHandler<ProbeCommand, int, ProbeHandler>();
        await using var app = builder.Build();
        app.UseRouting();
        app.UseOperationJournal();
        app.MapPost("/suppressed", async (ISender sender) =>
        {
            await sender.SendAsync(new ProbeCommand("private-command-payload"));
            return Results.NoContent();
        }).WithMetadata(new OperationLogSuppression("测试已明确排除的低价值操作"));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.PostAsync(new Uri("/suppressed", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        Assert.Empty(await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task RejectedCommand_UsesTrustedActor_AndDoesNotLogFailureText()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "command-probe");
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("trusted-command-user"));
        builder.Services.AddCommandHandler<RejectedCommand, RejectedHandler>();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .SendAsync(new RejectedCommand("private-forged-actor"));

        Assert.True(result.IsFailure);
        Assert.Equal("probe.rejected", result.Error.Code);
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var messages = await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        var observations = messages.Select(message => new SystemTextJsonIntegrationEventSerializer()
            .Deserialize<OperationObservedV1>(message.Payload)).ToArray();
        var finished = Assert.Single(observations, item => item.Phase == "finished");
        Assert.Equal("rejected", finished.Outcome);
        Assert.Equal("trusted-command-user", finished.ActorId);
        Assert.Null(finished.StatusCode);
        Assert.All(messages, message => Assert.DoesNotContain("private-", message.Payload, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HttpCommandAndItsNestedCommands_AreOneHttpOperation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "command-probe");
        builder.Services.AddCommandHandler<ProbeCommand, int, ProbeHandler>();
        builder.Services.AddCommandHandler<OuterCommand, int, OuterHandler>();
        await using var app = builder.Build();
        app.UseRouting();
        app.UseOperationJournal();
        app.MapPost("/commands", async (ISender sender) =>
        {
            var result = await sender.SendAsync(new OuterCommand());
            return result.IsSuccess && result.Value == 42 ? Results.NoContent() : Results.StatusCode(500);
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.PostAsync(new Uri("/commands", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var messages = await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow, timeout.Token);
            var observations = messages.Select(message => new SystemTextJsonIntegrationEventSerializer()
                .Deserialize<OperationObservedV1>(message.Payload)).ToArray();
            if (observations.Any(item => item.Kind == "http" && item.Phase == "finished"))
            {
                Assert.Equal(2, observations.Length);
                Assert.Single(observations.Select(item => item.OperationId).Distinct());
                Assert.All(observations, item => Assert.Equal("http", item.Kind));
                break;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task NestedCommands_ShareTheOuterOperation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "command-probe");
        builder.Services.AddCommandHandler<ProbeCommand, int, ProbeHandler>();
        builder.Services.AddCommandHandler<OuterCommand, int, OuterHandler>();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new OuterCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var messages = await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        Assert.Equal(2, messages.Count);
        var observations = messages.Select(message => new SystemTextJsonIntegrationEventSerializer()
            .Deserialize<OperationObservedV1>(message.Payload)).ToArray();
        Assert.Single(observations.Select(item => item.OperationId).Distinct());
        Assert.All(observations, item => Assert.Equal("command.OuterCommand", item.Metadata?.Action));
    }

    [Fact]
    public async Task StandaloneCommand_RecordsOneOperation_WithoutItsPayload()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "command-probe");
        builder.Services.AddCommandHandler<ProbeCommand, int, ProbeHandler>();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .SendAsync(new ProbeCommand("private-command-payload"));

        Assert.True(result.IsSuccess);
        Assert.Equal(21, result.Value);
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var messages = await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow);
        Assert.Equal(2, messages.Count);
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var observations = messages.Select(message => serializer.Deserialize<OperationObservedV1>(message.Payload)).ToArray();
        var started = Assert.Single(observations, item => item.Phase == "started");
        var finished = Assert.Single(observations, item => item.Phase == "finished");
        Assert.Equal(started.OperationId, finished.OperationId);
        Assert.NotEqual(started.EventId, finished.EventId);
        Assert.Equal("command", finished.Kind);
        Assert.Equal("command-probe", finished.Source);
        Assert.Equal("completed", finished.Outcome);
        Assert.Null(finished.StatusCode);
        Assert.Null(finished.HttpMethod);
        Assert.Null(finished.ActorId);
        Assert.All(messages, message => Assert.DoesNotContain("private-command-payload", message.Payload, StringComparison.Ordinal));
    }

    private sealed record ProbeCommand(string Secret) : ICommand<int>;
    private sealed record OutcomeCommand(string Outcome) : ICommand<int>;
    private sealed record ConcurrentCommand(Guid TaskId) : ICommand<Guid>, ITaskOperationCommand;

    private sealed class ConcurrentGate
    {
        public int Arrived;
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ConcurrentHandler(ConcurrentGate gate) : ICommandHandler<ConcurrentCommand, Guid>
    {
        public async Task<Result<Guid>> HandleAsync(ConcurrentCommand command, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref gate.Arrived) == 2) { gate.BothStarted.SetResult(); }
            await gate.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return Result.Success(command.TaskId);
        }
    }

    private sealed class OutcomeValidator : IRequestValidator<OutcomeCommand>
    {
        public Result Validate(OutcomeCommand request) => request.Outcome == "rejected"
            ? Result.Failure(new Error("probe.validation", "private-validation-failure")) : Result.Success();
    }

    private sealed class OutcomeHandler : ICommandHandler<OutcomeCommand, int>
    {
        public Task<Result<int>> HandleAsync(OutcomeCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Outcome is "failed" or "rejected") { throw new InvalidOperationException("private-command-failure"); }
            return Task.FromResult(Result.Success(21));
        }
    }

    private sealed class UnavailableJournal : IOperationJournal
    {
        public Task<Result> AppendAsync(OperationObservedV1 observation, CancellationToken cancellationToken = default) =>
            Task.FromException<Result>(new IOException("private-journal-failure"));
    }
    private sealed record LaunchCommand : ICommand<Task<int>>;

    private sealed class LaunchHandler(ISender sender, TaskCompletionSource release) : ICommandHandler<LaunchCommand, Task<int>>
    {
        public Task<Result<Task<int>>> HandleAsync(LaunchCommand command, CancellationToken cancellationToken = default)
        {
            var work = Task.Run(async () =>
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return (await sender.SendAsync(new ProbeCommand("private-detached"), cancellationToken)).Value;
            }, cancellationToken);
            return Task.FromResult(Result.Success(work));
        }
    }
    private sealed record RejectedCommand(string ActorId) : ICommand;

    private sealed class RejectedHandler : ICommandHandler<RejectedCommand>
    {
        public Task<Result> HandleAsync(RejectedCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure(new Error("probe.rejected", "private-rejection-description")));
    }
    private sealed record OuterCommand : ICommand<int>;

    private sealed class OuterHandler(ISender sender) : ICommandHandler<OuterCommand, int>
    {
        public async Task<Result<int>> HandleAsync(OuterCommand command, CancellationToken cancellationToken = default)
        {
            var first = await sender.SendAsync(new ProbeCommand("private-first"), cancellationToken);
            var second = await sender.SendAsync(new ProbeCommand("private-second"), cancellationToken);
            return Result.Success(first.Value + second.Value);
        }
    }

    private sealed class ProbeHandler : ICommandHandler<ProbeCommand, int>
    {
        public Task<Result<int>> HandleAsync(ProbeCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(21));
    }
}
