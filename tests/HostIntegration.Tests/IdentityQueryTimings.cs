using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.Identity.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace NexusStackNext.HostIntegration.Tests;

// Observe only the current query's Identity and journal contexts. Never publish SQL or event payloads.
internal sealed class IdentityQueryTimings(ITestOutputHelper output) :
    IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<ActivityTraceId, Measurement> _measurements = new();
    private IDisposable? _listeners;
    private ActivityListener? _requests;
    private bool _disposed;
    private long _commands;

    internal long Commands => Interlocked.Read(ref _commands);

    internal void Start()
    {
        _requests = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.PropagationData,
            ActivityStopped = activity =>
            {
                if (_measurements.TryGetValue(activity.TraceId, out var measurement)) { measurement.ServerCompleted(activity.Duration); }
            },
        };
        ActivitySource.AddActivityListener(_requests);
        _listeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    internal Measurement Measure(QueryPhase phase)
    {
        var measurement = new Measurement(this, phase, output);
        if (!_measurements.TryAdd(measurement.TraceId, measurement)) { throw new InvalidOperationException("Duplicate query trace."); }
        return measurement;
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != "Microsoft.EntityFrameworkCore") { return; }
        lock (_subscriptions)
        {
            if (!_disposed) { _subscriptions.Add(listener.Subscribe(this)); }
        }
    }

    public void OnNext(KeyValuePair<string, object?> notification)
    {
        if (Activity.Current is not { } activity || !_measurements.TryGetValue(activity.TraceId, out var measurement)) { return; }
        if (notification.Value is CommandExecutedEventData { Context: IdentityDbContext } command)
        {
            Interlocked.Increment(ref _commands);
            measurement.Command(command.Duration);
        }
        else if (notification.Value is ConnectionEndEventData { Context: IdentityDbContext } connection &&
            notification.Key == "Microsoft.EntityFrameworkCore.Database.Connection.ConnectionOpened")
        {
            measurement.Connection(connection.Duration);
        }
        else if (notification.Value is CommandExecutedEventData { Context: OperationJournalDbContext } journal)
        {
            measurement.JournalCommand(journal.Duration);
        }
        else if (notification.Value is TransactionEndEventData { Context: OperationJournalDbContext } transaction &&
            notification.Key is "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionStarted" or
                "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitted")
        {
            measurement.JournalTransaction(transaction.Duration);
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void Dispose()
    {
        _listeners?.Dispose();
        _requests?.Dispose();
        lock (_subscriptions)
        {
            _disposed = true;
            foreach (var subscription in _subscriptions) { subscription.Dispose(); }
            _subscriptions.Clear();
        }
    }

    internal sealed class Measurement(IdentityQueryTimings owner, QueryPhase phase, ITestOutputHelper output) : IDisposable
    {
        private readonly Activity _activity = new Activity("query-measurement").SetIdFormat(ActivityIdFormat.W3C).Start();
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _commands;
        private long _commandTicks;
        private long _connections;
        private long _connectionTicks;
        private long _journalCommands;
        private long _journalTicks;
        private long _journalTransactions;
        private long _journalTransactionTicks;
        private readonly TaskCompletionSource<TimeSpan> _server = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TimeSpan _serverDuration;

        internal ActivityTraceId TraceId => _activity.TraceId;
        internal long Commands => Interlocked.Read(ref _commands);
        internal long JournalCommands => Interlocked.Read(ref _journalCommands);

        internal void ServerCompleted(TimeSpan duration) => _server.TrySetResult(duration);

        internal async Task CompleteHttpAsync()
        {
            // Keep the client boundary separate; Finished journal persistence can outlive the response.
            _watch.Stop();
            _serverDuration = await _server.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        internal void JournalCommand(TimeSpan duration)
        {
            Interlocked.Increment(ref _journalCommands);
            Interlocked.Add(ref _journalTicks, duration.Ticks);
        }

        internal void JournalTransaction(TimeSpan duration)
        {
            Interlocked.Increment(ref _journalTransactions);
            Interlocked.Add(ref _journalTransactionTicks, duration.Ticks);
        }

        internal void Command(TimeSpan duration)
        {
            Interlocked.Increment(ref _commands);
            Interlocked.Add(ref _commandTicks, duration.Ticks);
        }

        internal void Connection(TimeSpan duration)
        {
            Interlocked.Increment(ref _connections);
            Interlocked.Add(ref _connectionTicks, duration.Ticks);
        }

        public void Dispose()
        {
            _watch.Stop();
            owner._measurements.TryRemove(TraceId, out _);
            _activity.Dispose();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"QUERY_TIMING phase={phase} total_ms={_watch.Elapsed.TotalMilliseconds:F1} commands={Interlocked.Read(ref _commands)} sql_ms={TimeSpan.FromTicks(Interlocked.Read(ref _commandTicks)).TotalMilliseconds:F1} connections={Interlocked.Read(ref _connections)} connection_ms={TimeSpan.FromTicks(Interlocked.Read(ref _connectionTicks)).TotalMilliseconds:F1} server_ms={_serverDuration.TotalMilliseconds:F1} journal_commands={Interlocked.Read(ref _journalCommands)} journal_sql_ms={TimeSpan.FromTicks(Interlocked.Read(ref _journalTicks)).TotalMilliseconds:F1} journal_transactions={Interlocked.Read(ref _journalTransactions)} journal_transaction_ms={TimeSpan.FromTicks(Interlocked.Read(ref _journalTransactionTicks)).TotalMilliseconds:F1}"));
        }
    }
}

internal enum QueryPhase
{
    EmptyCold,
    EmptyWarm,
    GrantedCold,
    GrantedWarm,
    SessionCurrent,
    SessionAfterLogout,
    HttpCold,
    HttpWarm,
    HttpRevoked,
}
