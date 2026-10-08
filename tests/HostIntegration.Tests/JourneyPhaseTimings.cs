using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace NexusStackNext.HostIntegration.Tests;

// Only fixed phase names and durations go to the private per-case TRX output.
internal sealed class JourneyPhaseTimings : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ActivityListener _permitWaits;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private long _permitWaitTicks;
    private TimeSpan _previous;
    private JourneyPhase _phase = JourneyPhase.Other;
    private bool _disposed;

    internal JourneyPhaseTimings(ITestOutputHelper output)
    {
        _output = output;
        // Host tests serialize cases within each worker, so this listener belongs to one active case.
        _permitWaits = new ActivityListener
        {
            ShouldListenTo = source => source.Name == JourneyDatabaseOperation.PermitWaitSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => Interlocked.Add(ref _permitWaitTicks, activity.Duration.Ticks),
        };
        ActivitySource.AddActivityListener(_permitWaits);
    }

    internal void MoveTo(JourneyPhase phase)
    {
        WritePhase();
        _phase = phase;
    }

    private void WritePhase()
    {
        var now = _watch.Elapsed;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TEST_PHASE phase={_phase} elapsed_ms={(now - _previous).TotalMilliseconds:F1}"));
        _previous = now;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _permitWaits.Dispose();
        WritePhase();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TEST_WAIT kind=DatabasePermit elapsed_ms={TimeSpan.FromTicks(Interlocked.Read(ref _permitWaitTicks)).TotalMilliseconds:F1}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TEST_PHASE_TOTAL elapsed_ms={_watch.Elapsed.TotalMilliseconds:F1}"));
    }
}

internal enum JourneyPhase
{
    Other,
    DatabasePreparation,
    HostStartup,
    PermissionPreparation,
    FactPreparation,
    RecoveryAssertions,
    SessionAssertions,
    BrokerTopology,
    FixtureStartup,
    SourceStartup,
    SourceAssertions,
    SourceCleanup,
    BrokerReceive,
    CentralStartup,
    CentralAssertions,
    CentralCleanup,
    BrokerCleanup,
    RemainingCleanup,
}
