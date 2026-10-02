using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Endpoints;

/// <summary>
/// 文件存储的就绪检查：**写入一个探针再删掉**。
///
/// <para>探的是<b>端口本身</b>而不是某个具体实现的内部状态（比如磁盘根目录是否可写）——
/// 于是换成本地磁盘、对象存储、还是别的东西，这条检查都不用改。
/// 端口没变，它就还有效。</para>
///
/// <para>与网关同理：Files 此前只报"进程活着"，而磁盘满了、权限变了、对象存储挂了，
/// 它都会照样报健康。</para>
/// </summary>
/// <param name="store">字节存储端口。</param>
public sealed class FileStorageHealthCheck(IFileStore store) : IHealthCheck
{
    private static readonly byte[] Probe = "health-probe"u8.ToArray();

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        string? key = null;

        try
        {
            await using (var write = await store
                .WriteAsync(new MemoryStream(Probe), "application/octet-stream", cancellationToken)
                .ConfigureAwait(false))
            {
                key = write.StorageKey;
            }

            await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("存储可写可删。");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("文件存储不可用。", exception);
        }
        finally
        {
            // 删除失败时尽力清理探针，但**不让它影响判定**：上面已经判定过了。
            if (key is not null)
            {
                try
                {
                    await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 探针残留一个几十字节的文件，比把就绪状态搞错要好。
                }
            }
        }
    }
}
