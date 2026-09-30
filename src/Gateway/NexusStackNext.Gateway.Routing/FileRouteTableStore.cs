using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Gateway.Routing;

/// <summary>
/// 基于文件的路由表存储。
///
/// <para><b>写入保证：读者永远读不到"写了一半"的文档。</b>
/// 内容先写到同目录下的临时文件，再整体替换目标。临时文件刻意放在<b>同一目录</b>——
/// 跨卷替换会退化成"复制 + 删除"，那条保证就没了。</para>
///
/// <para><b>写入不保证：读者不会观察到替换的瞬间。</b>
/// 这是实测出来的，不是推测：在 Windows 上，目标文件被读者打开时，
/// <c>File.Move(overwrite: true)</c> 会以 <c>Access to the path is denied</c> 失败；
/// 改用 <c>File.Replace</c>（Win32 <c>ReplaceFile</c>）后写者能成功，
/// 但读者会短暂看到 <b>文件不存在</b>或<b>被占用</b>——替换期间目录项是空窗的。
/// 因此读取侧在有限预算内重试，把这两种瞬时状态吸收掉。</para>
///
/// <para>结论写清楚，是因为"原子写"这个词很容易让人以为连"看见旧还是新"都保证了。
/// 它保证的是<b>内容的完整性</b>，不是<b>目录项的连续性</b>。</para>
///
/// <para><b>它的边界：</b>这是单机默认实现。多副本部署时"每个副本各有一份文件"仍然成立，
/// 因此它只适合单写者场景；多副本请替换为配置中心或数据库实现（端口就是为此留的）。</para>
/// </summary>
public sealed class FileRouteTableStore : IRouteTableStore
{
    private const string TempSuffix = ".tmp";

    /// <summary>读取侧吸收"替换瞬间"的预算：尝试次数与每次间隔。</summary>
    private const int ReadMaxAttempts = 12;
    private const int ReadRetryDelayMilliseconds = 15;

    /// <summary>构造文件存储。</summary>
    /// <param name="filePath">路由配置文件路径。</param>
    /// <exception cref="ArgumentException">路径为空。</exception>
    public FileRouteTableStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    /// <summary>路由配置文件路径。</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public async Task<Result<GatewayRouteTable>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Error? lastError = null;

        for (var attempt = 1; attempt <= ReadMaxAttempts; attempt++)
        {
            if (!File.Exists(FilePath))
            {
                // 可能是真的不存在，也可能正落在替换的空窗里。预算用完才下结论。
                lastError = RouteTableStoreErrors.FileMissing(FilePath);
            }
            else
            {
                try
                {
                    string json;

                    // <b>必须以 FileShare.Delete 打开。</b>
                    // 否则写者的替换会失败——读者越多，写者越写不进去。
                    await using (var stream = new FileStream(
                        FilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        bufferSize: 4096,
                        useAsync: true))
                    {
                        using var reader = new StreamReader(stream);
                        json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // 读到的永远是完整文档：格式错误就是**真的**格式错误，不是读了一半。
                    return GatewayRouteTable.FromJson(json);
                }
                catch (IOException exception)
                {
                    lastError = RouteTableStoreErrors.ReadFailed(exception.Message);
                }
                catch (UnauthorizedAccessException exception)
                {
                    lastError = RouteTableStoreErrors.ReadFailed(exception.Message);
                }
            }

            if (attempt < ReadMaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ReadRetryDelayMilliseconds), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return Result.Failure<GatewayRouteTable>(lastError!);
    }

    /// <inheritdoc />
    public async Task<Result> SaveAsync(GatewayRouteTable table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);

        var fullPath = Path.GetFullPath(FilePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Result.Failure(RouteTableStoreErrors.PathEmpty());
        }

        var tempPath = fullPath + TempSuffix;

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(tempPath, table.ToJson(), cancellationToken).ConfigureAwait(false);

            await ReplaceAtomicallyAsync(tempPath, fullPath).ConfigureAwait(false);
            return Result.Success();
        }
        catch (IOException exception)
        {
            return Result.Failure(RouteTableStoreErrors.WriteFailed(exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure(RouteTableStoreErrors.WriteFailed(exception.Message));
        }
        finally
        {
            // 失败时不留垃圾；成功时文件已被替换走，这里什么也不做。
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // 清理失败不是保存失败——目标要么是旧的、要么是新的，都是完整的。
                }
            }
        }
    }

    /// <summary>
    /// 用整体替换写入目标。
    /// <para>
    /// <b>刻意不用 <c>File.Move(source, destination, overwrite: true)</c>。</b>
    /// 实测：读者持有目标时它会以 <c>Access to the path is denied</c> 失败，
    /// 而后果很难查——读流量一稳定，写者就永远写不进去。
    /// </para>
    /// <para>
    /// <c>File.Replace</c>（Win32 <c>ReplaceFile</c>）就是为"替换一个可能正被打开的文件"设计的。
    /// 目标不存在时退回普通 move。外面再包一层重试，覆盖替换瞬间的短暂冲突。
    /// </para>
    /// </summary>
    private static async Task ReplaceAtomicallyAsync(string sourcePath, string destinationPath)
    {
        const int MaxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(sourcePath, destinationPath);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException && attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 5)).ConfigureAwait(false);
            }
        }
    }
}
