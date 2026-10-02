using System.Net;

namespace NexusStackNext.HostIntegration.Tests;

internal sealed class PausedUploadContent(CancellationToken pauseCancellation) : HttpContent
{
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Complete() => _resume.TrySetResult();

    protected override bool TryComputeLength(out long length) { length = 0; return false; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        await _resume.Task.WaitAsync(pauseCancellation);
        await stream.WriteAsync(new byte[] { 2 }, cancellationToken);
    }
}
