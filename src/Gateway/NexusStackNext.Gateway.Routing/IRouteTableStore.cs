using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Gateway.Routing;

/// <summary>
/// 路由表的持久化端口。
/// <para>
/// 之所以先有端口而不是直接写文件：参照仓库把 `proxy-config.json` 放在
/// <c>AppContext.BaseDirectory</c>（<c>JsonProxyConfigStore.cs:44</c>），
/// 于是**每个副本各有一份**、写入**不是原子的**（<c>EnsureConfigFileExists:47</c> 还在 try 之外），
/// 而仓库里那份配置文件根本没有任何 csproj 引用它——一个死文件。
/// </para>
/// <para>
/// 端口把"存哪儿"留成可替换的决定（单机文件 / 配置中心 / 数据库），
/// 文件适配器保证完整文档写入；共享存储并不自动提供多写者协调，后者需要另行定义版本契约。
/// </para>
/// </summary>
public interface IRouteTableStore
{
    /// <summary>载入路由表。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>合法时返回路由表；否则返回带原因的失败。</returns>
    Task<Result<GatewayRouteTable>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存路由表。</summary>
    /// <param name="table">路由表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功或失败原因。</returns>
    Task<Result> SaveAsync(GatewayRouteTable table, CancellationToken cancellationToken = default);
}

/// <summary>路由持久化的失败。</summary>
public static class RouteTableStoreErrors
{
    /// <summary>文件不存在。</summary>
    /// <param name="path">路径。</param>
    /// <returns>错误。</returns>
    public static Error FileMissing(string path) =>
        new("gateway.route_table.file_missing", $"路由配置文件不存在：{path}");

    /// <summary>读取失败。</summary>
    /// <param name="reason">原因。</param>
    /// <returns>错误。</returns>
    public static Error ReadFailed(string reason) =>
        new("gateway.route_table.read_failed", $"读取路由配置失败：{reason}");

    /// <summary>写入失败。</summary>
    /// <param name="reason">原因。</param>
    /// <returns>错误。</returns>
    public static Error WriteFailed(string reason) =>
        new("gateway.route_table.write_failed", $"写入路由配置失败：{reason}");

    /// <summary>路径为空。</summary>
    /// <returns>错误。</returns>
    public static Error PathEmpty() =>
        new("gateway.route_table.path_empty", "路由配置路径不能为空。");
}
