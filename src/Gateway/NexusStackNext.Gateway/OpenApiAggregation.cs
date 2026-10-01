using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.Gateway;

/// <summary>一个文档来源的取用结果。</summary>
/// <param name="Name">来源名（<c>gateway</c> 或 cluster id）。</param>
/// <param name="Url">文档地址。</param>
/// <param name="Ok">是否取到。</param>
/// <param name="PathCount">路径条数。</param>
/// <param name="Detail">失败原因；成功时为 <c>null</c>。</param>
public sealed record OpenApiSourceStatus(string Name, string Url, bool Ok, int PathCount, string? Detail);

/// <summary>聚合后的文档与各来源的状态。</summary>
/// <param name="Json">合并后的 OpenAPI 文档。</param>
/// <param name="Sources">各来源状态。</param>
public sealed record AggregatedOpenApi(string Json, IReadOnlyList<OpenApiSourceStatus> Sources);

/// <summary>
/// 把网关自己与各后端的 OpenAPI 文档合并成一份。
///
/// <para><b>为什么在网关上聚合。</b>部署不变量说"业务服务不对外暴露，边缘是唯一入口"——
/// 所以生产环境里能对外提供文档的**只有边缘**。把界面挂在业务服务上会绕过边缘，
/// 挂在网关上则与这条不变量一致。</para>
///
/// <para><b>所有来源统一走 HTTP，包括网关自己。</b>网关自己的文档由
/// <c>MapOpenApi("/openapi/gateway.json")</c> 暴露，聚合器像取后端一样取它。
/// 一套代码路径，没有"自己"这个特例。</para>
///
/// <para><b>一个来源失败不会让整份文档挂掉</b>，但**也不会被藏起来**：
/// 失败的来源出现在 <see cref="AggregatedOpenApi.Sources"/> 里，
/// 而 <c>/gateway/openapi/sources</c> 端点把这份状态直接暴露出来。
/// "文档少了几个接口"必须是**看得见**的，否则它和"接口本来就不存在"分不开。</para>
/// </summary>
/// <param name="httpClientFactory">HTTP 客户端工厂。</param>
/// <param name="configuration">进程接受的路由配置，各后端地址与缓存版本从它来。</param>
/// <param name="server">用来发现网关自己监听的地址。</param>
/// <param name="logger">日志。</param>
public sealed partial class DownstreamOpenApiAggregator(
    IHttpClientFactory httpClientFactory,
    GatewayRouteConfiguration configuration,
    IServer server,
    ILogger<DownstreamOpenApiAggregator> logger) : IDisposable
{
    /// <summary>缓存时长。文档变化很慢，而每次请求都去取一轮下游是浪费。</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>单个来源的取用超时。</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedDocument? _cached;

    /// <summary>取得合并后的文档（带缓存）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>合并结果与各来源状态。</returns>
    public async Task<AggregatedOpenApi> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cached) is { } cached && ReferenceEquals(cached.Table, configuration.Current) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < CacheDuration)
        {
            return cached.Document;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双重检查：等在锁上的那一个不该再取一遍。
            var table = configuration.Current;
            if (_cached is { } fresh && ReferenceEquals(fresh.Table, table) && DateTimeOffset.UtcNow - fresh.CreatedAt < CacheDuration)
            {
                return fresh.Document;
            }

            var result = await BuildAsync(table, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cached, new CachedDocument(table, result, DateTimeOffset.UtcNow));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AggregatedOpenApi> BuildAsync(GatewayRouteTable table, CancellationToken cancellationToken)
    {
        var merged = new JsonObject
        {
            ["openapi"] = "3.1.1",
            ["info"] = new JsonObject
            {
                ["title"] = "NexusStackNext 边缘聚合文档",
                ["version"] = "v1",
                ["description"] = "由网关聚合自身与各后端的 OpenAPI 文档。来源状态见 /gateway/openapi/sources。",
            },
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
        };

        var mergedPaths = (JsonObject)merged["paths"]!;
        var mergedSchemas = (JsonObject)merged["components"]!["schemas"]!;
        var statuses = new List<OpenApiSourceStatus>();

        foreach (var (name, url) in Sources(table))
        {
            var (document, failure) = await FetchAsync(url, cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                LogSourceUnavailable(logger, name, url, failure);
                statuses.Add(new OpenApiSourceStatus(name, url, Ok: false, PathCount: 0, Detail: failure));
                continue;
            }

            var pathCount = MergeInto(mergedPaths, mergedSchemas, document, name);
            statuses.Add(new OpenApiSourceStatus(name, url, Ok: true, PathCount: pathCount, Detail: null));
            LogSourceMerged(logger, name, url, pathCount);
        }

        return new AggregatedOpenApi(merged.ToJsonString(JsonOptions), statuses);
    }

    /// <summary>文档来源：网关自己 + 每个 cluster 的第一个目标。</summary>
    private IEnumerable<(string Name, string Url)> Sources(GatewayRouteTable routeTable)
    {
        if (SelfBaseUrl() is { } self)
        {
            yield return ("gateway", $"{self}/openapi/gateway.json");
        }
        else
        {
            LogSelfAddressUnknown(logger);
        }

        foreach (var cluster in routeTable.Clusters)
        {
            if (cluster.Destinations.Count == 0)
            {
                continue;
            }

            yield return (cluster.ClusterId, $"{cluster.Destinations[0].Address.TrimEnd('/')}/openapi/v1.json");
        }
    }

    private sealed record CachedDocument(GatewayRouteTable Table, AggregatedOpenApi Document, DateTimeOffset CreatedAt);

    /// <summary>从 <see cref="IServerAddressesFeature"/> 取网关自己监听的 http 地址。</summary>
    private string? SelfBaseUrl()
    {
        var feature = server.Features.Get<IServerAddressesFeature>();

        return feature?.Addresses.FirstOrDefault(
            static address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(JsonObject? Document, string? Failure)> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(DownstreamOpenApiAggregator));
            client.Timeout = FetchTimeout;

            var text = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(text) as JsonObject;

            return node is null ? (null, "返回的不是 JSON 对象") : (node, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, exception.Message);
        }
    }

    /// <summary>
    /// 把一份下游文档并进合并结果。
    ///
    /// <para><b>schema 名一律加来源前缀。</b>两家后端各有一个叫 <c>ProblemDetails</c> 的模型时，
    /// 直接合并会让其中一家的 <c>$ref</c> 指向另一家的结构——**而且不会报错**，
    /// 只是文档里的字段悄悄错了。加前缀让这件事在结构上不可能发生，
    /// 顺带让"这个模型来自哪个服务"在界面上看得见。</para>
    /// </summary>
    /// <returns>并入的路径条数。</returns>
    private int MergeInto(JsonObject mergedPaths, JsonObject mergedSchemas, JsonObject source, string sourceName)
    {
        // 在克隆上动手：来源文档本身还要用于状态统计，不该被就地改写。
        var document = (JsonObject)source.DeepClone()!;
        var merged = 0;

        // **必须先把引用改名，再拷贝任何东西。**
        // 反过来的话，先拷出去的 paths 里留着旧引用，而 schema 键已经带了前缀——
        // 文档里就会出现一批指向不存在模型的 $ref。实测踩过这个顺序。
        var schemas = document["components"]?["schemas"] as JsonObject;

        if (schemas is { Count: > 0 })
        {
            RenameSchemaReferences(document, sourceName, schemas);
        }

        if (document["paths"] is JsonObject paths)
        {
            foreach (var (path, item) in paths.ToList())
            {
                // 两条路由指向同一个路径——保留先到的，并把冲突**记下来**。
                // 悄悄覆盖会让其中一个来源的接口从文档里消失，而没有任何东西会响。
                if (mergedPaths.ContainsKey(path))
                {
                    LogPathConflict(logger, path, sourceName);
                    continue;
                }

                mergedPaths[path] = item?.DeepClone();
                merged++;
            }
        }

        if (schemas is { Count: > 0 })
        {
            foreach (var (name, schema) in schemas.ToList())
            {
                mergedSchemas[$"{sourceName}.{name}"] = schema?.DeepClone();
            }
        }

        return merged;
    }

    /// <summary>
    /// 把文档里指向<b>本来源自己的</b> schema 的 <c>$ref</c> 改成带来源前缀的形式。
    ///
    /// <para>只改"目标确实在本来源里"的那些引用——否则会把外部引用也改写，
    /// 制造出新的悬空引用。</para>
    /// </summary>
    /// <param name="node">要遍历的节点。</param>
    /// <param name="prefix">来源前缀。</param>
    /// <param name="schemas">本来源的 schema 集合。</param>
    private static void RenameSchemaReferences(JsonNode? node, string prefix, JsonObject schemas)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var (key, value) in jsonObject.ToList())
                {
                    if (key == "$ref"
                        && value is JsonValue jsonValue
                        && jsonValue.TryGetValue<string>(out var reference)
                        && reference.StartsWith(SchemaReferencePrefix, StringComparison.Ordinal)
                        && schemas.ContainsKey(reference[SchemaReferencePrefix.Length..]))
                    {
                        jsonObject[key] = SchemaReferencePrefix + prefix + "." + reference[SchemaReferencePrefix.Length..];
                        continue;
                    }

                    RenameSchemaReferences(value, prefix, schemas);
                }

                break;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    RenameSchemaReferences(item, prefix, schemas);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>schema 引用在文档里的固定前缀。</summary>
    private const string SchemaReferencePrefix = "#/components/schemas/";

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>组装一条诊断行，便于把来源状态打成日志或返回给调用方。</summary>
    /// <param name="status">来源状态。</param>
    /// <returns>一行文本。</returns>
    public static string Describe(OpenApiSourceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.Ok
            ? string.Create(CultureInfo.InvariantCulture, $"{status.Name}: {status.PathCount} 条路径（{status.Url}）")
            : string.Create(CultureInfo.InvariantCulture, $"{status.Name}: 取不到（{status.Url}）—— {status.Detail}");
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "聚合 OpenAPI 文档时取不到来源 {Name}（{Url}）：{Failure}")]
    private static partial void LogSourceUnavailable(ILogger logger, string name, string url, string? failure);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "已聚合 OpenAPI 来源 {Name}（{Url}）：{PathCount} 条路径。")]
    private static partial void LogSourceMerged(ILogger logger, string name, string url, int pathCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "发现不了网关自己监听的地址，聚合文档里将不含网关自身的接口。")]
    private static partial void LogSelfAddressUnknown(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "聚合 OpenAPI 时路径冲突：{Path} 已存在，来源 {Source} 的这份被跳过。")]
    private static partial void LogPathConflict(ILogger logger, string path, string source);
}
