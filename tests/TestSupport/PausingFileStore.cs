using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.TestSupport;

/// <summary>在真实字节操作完成后暂停一次返回，用公开存储端口安排提交争用。</summary>
/// <param name="inner">仍执行真实读写与写入保护的存储。</param>
public sealed class PausingFileStore(IFileStore inner) : IFileStore
{
    private readonly TaskCompletionSource<string> _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _operation;

    /// <summary>字节操作已完成；结果为仍受原保护协议约束的存储句柄。</summary>
    public Task<string> Paused => _paused.Task;

    /// <summary>暂停下一次成功写入的返回；期间仍持有真实写入保护。</summary>
    public void PauseNextWrite() => _operation = "write";

    /// <summary>暂停下一次成功删除的返回。</summary>
    public void PauseNextDelete() => _operation = "delete";

    /// <summary>释放暂停；测试须在 finally 中释放并等待调用者结束。</summary>
    public void Resume() => _resume.TrySetResult();

    /// <inheritdoc />
    public async Task<FileWrite> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var write = await inner.WriteAsync(content, contentType, cancellationToken).ConfigureAwait(false);
        try
        {
            await PauseAsync("write", write.StorageKey, cancellationToken).ConfigureAwait(false);
            return write;
        }
        catch
        {
            await write.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.OpenReadAsync(storageKey, cancellationToken);

    /// <inheritdoc />
    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        await inner.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
        await PauseAsync("delete", storageKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task PauseAsync(string operation, string storageKey, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operation, null, operation) != operation) { return; }
        _paused.TrySetResult(storageKey);
        await _resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
