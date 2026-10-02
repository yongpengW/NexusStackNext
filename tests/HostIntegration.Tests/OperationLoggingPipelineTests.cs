using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationLoggingPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalAndDiagnosticsFailure_PreserveBusinessResponseAndFailureCount(bool failMetric)
    {
        using var listener = new MeterListener();
        if (failMetric)
        {
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "NexusStackNext.OperationJournal") { owner.EnableMeasurementEvents(instrument); }
            };
            listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("injected meter failure"));
            listener.Start();
        }
        var journal = new ProbeJournal(fail: true);
        await using var app = CreateApp(journal, failLogger: !failMetric);
        app.MapPost("/api/probe", () => Results.NoContent());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync(new Uri("/api/probe", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, app.Services.GetRequiredService<OperationJournalStatus>().FailureCount);
    }

    [Fact]
    public async Task ClientDisconnect_RecordsCanceledAfterExceptionHandlerConvertsTheException()
    {
        var journal = new ProbeJournal(fail: false);
        await using var app = CreateApp(journal);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/api/probe", async (HttpContext context) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var cancel = new CancellationTokenSource();
        var request = client.GetAsync(new Uri("/api/probe", UriKind.Relative), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var finished = await journal.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("canceled", finished.Outcome);
        Assert.Equal(499, finished.StatusCode);
        Assert.Null(finished.ActorId);
    }

    private static WebApplication CreateApp(ProbeJournal journal, bool failLogger = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (failLogger) { builder.Logging.AddProvider(new ThrowingProvider()); }
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OperationJournal:Storage:Provider"] = "Memory" });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddApiResponseContract();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "probe");
        builder.Services.AddSingleton<IOperationJournal>(journal);
        var app = builder.Build();
        app.UseRouting();
        app.UseOperationJournal();
        app.UseExceptionHandler();
        app.UseApiResponseContract();
        return app;
    }

    private sealed class ProbeJournal(bool fail) : IOperationJournal
    {
        public TaskCompletionSource<OperationObservedV1> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Result> AppendAsync(OperationObservedV1 observation, CancellationToken cancellationToken = default)
        {
            if (observation.Phase == "finished") { Finished.TrySetResult(observation); }
            return Task.FromResult(fail ? Result.Failure(new Error("probe.journal_failure", "injected")) : Result.Success());
        }
    }

    private sealed class ThrowingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();
        public void Dispose() { }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 10) { throw new InvalidOperationException("injected diagnostic failure"); }
        }
    }
}
