namespace NexusStackNext.HostIntegration.Tests;

// One controller owns the workload; its workers serialize expensive database preparation.
internal static class JourneyDatabaseOperation
{
    internal static async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        await using var lease = await EnterAsync(preparation: true, cancellationToken: cancellationToken);
        await operation();
    }

    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await using var lease = await EnterAsync(preparation: true, cancellationToken: cancellationToken);
        return await operation();
    }

    internal static async Task<FileStream?> EnterAsync(bool preparation = false, CancellationToken cancellationToken = default)
    {
        var path = Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_DDL_GUARD");
        if (string.IsNullOrEmpty(path)) { return null; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            CheckPreparation(preparation);
            try
            {
                var lease = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                try { CheckPreparation(preparation); return lease; }
                catch { lease.Dispose(); throw; }
            }
            catch (IOException) { await Task.Delay(50, deadline.Token); }
        }
    }

    private static void CheckPreparation(bool preparation)
    {
        var stop = Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_LOAD_STOP");
        if (preparation && !string.IsNullOrEmpty(stop) && File.Exists(stop))
        { throw new InvalidOperationException("LOCAL_RESOURCE_STOP: new database preparation refused; owned cleanup remains available."); }
    }
}
