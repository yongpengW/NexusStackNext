namespace NexusStackNext.Files.Application;

/// <summary>同一宿主所有文件上传共享的资源上限。</summary>
public sealed class FileUploadLimits
{
    private int _activeUploads;

    /// <summary>构造上传限制。</summary>
    /// <param name="maxBytes">每份文件的最大字节数。</param>
    /// <param name="maxConcurrentUploads">同时写入并提交元数据的请求数。</param>
    public FileUploadLimits(long maxBytes = 64 * 1024 * 1024, int maxConcurrentUploads = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentUploads);
        MaxBytes = maxBytes;
        MaxConcurrentUploads = maxConcurrentUploads;
    }

    /// <summary>每份文件的最大字节数。</summary>
    public long MaxBytes { get; }

    /// <summary>每个宿主允许同时处理的上传数；超过时直接拒绝，不排无限等待队列。</summary>
    public int MaxConcurrentUploads { get; }

    internal bool TryEnter()
    {
        while (true)
        {
            var active = Volatile.Read(ref _activeUploads);
            if (active >= MaxConcurrentUploads) { return false; }
            if (Interlocked.CompareExchange(ref _activeUploads, active + 1, active) == active) { return true; }
        }
    }

    internal void Exit() => Interlocked.Decrement(ref _activeUploads);
}

internal sealed class FileUploadLimitException() : IOException("文件超过上传大小限制。");

// 计数发生在实际读取时，Content-Length 缺失或不可信也不能绕过上限。
// 不拥有底层请求流；它的释放仍由调用方负责。
internal sealed class UploadReadStream(Stream source, long maxBytes) : Stream
{
    public long BytesRead { get; private set; }
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = source.Read(buffer[..ReadSize(buffer.Length)]);
        Record(count);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await source.ReadAsync(buffer[..ReadSize(buffer.Length)], cancellationToken).ConfigureAwait(false);
        Record(count);
        return count;
    }

    private int ReadSize(int requested) => (int)Math.Min(requested, Math.Min(int.MaxValue, maxBytes - BytesRead) + 1);

    private void Record(int count)
    {
        if (count > maxBytes - BytesRead) { throw new FileUploadLimitException(); }
        BytesRead += count;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
