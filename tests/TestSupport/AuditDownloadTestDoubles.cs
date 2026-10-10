using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.TestSupport;

internal sealed class DownloadAccess(string outcome) : IRequestAccessValidator
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PermissionKey? RequestedPermission { get; private set; }
    public async Task<Result<bool>> ValidateAsync(string userId, long? sessionVersion, PermissionKey required, CancellationToken cancellationToken = default)
    {
        RequestedPermission = required;
        Entered.TrySetResult();
        if (outcome == "wait") { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
        return outcome switch
        {
            "invalid" => Result.Failure<bool>(SessionValidationErrors.Invalid),
            "unavailable" => Result.Failure<bool>(SessionValidationErrors.Unavailable),
            _ => Result.Success(false),
        };
    }
}

internal sealed class ObservedFileStore(IFileStore inner) : IFileStore
{
    public ObservedReadStream? Opened { get; private set; }
    public Task<FileWrite> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default) => inner.WriteAsync(content, contentType, cancellationToken);
    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => inner.DeleteAsync(storageKey, cancellationToken);
    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Opened = new ObservedReadStream(await inner.OpenReadAsync(storageKey, cancellationToken).ConfigureAwait(false));
}

internal sealed class ObservedReadStream(Stream inner) : Stream
{
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Reads { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override int Read(byte[] buffer, int offset, int count) { Reads++; return inner.Read(buffer, offset, count); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Reads++; return inner.ReadAsync(buffer, cancellationToken); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); Closed.TrySetResult(); }
        base.Dispose(disposing);
    }
}
