namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>统一分页成功响应，data 直接包含本页条目。</summary>
/// <typeparam name="T">条目类型。</typeparam>
/// <param name="Data">本页条目。</param>
/// <param name="Total">符合查询条件的总条数。</param>
/// <param name="Page">从 1 开始的页码。</param>
/// <param name="Limit">每页大小。</param>
/// <param name="Timestamp">响应时间。</param>
/// <param name="TraceId">调用追踪标识。</param>
public sealed record ApiPage<T>(IReadOnlyList<T> Data, long Total, int Page, int Limit, long Timestamp, string TraceId)
    : ApiResponse<IReadOnlyList<T>>(Data, 200, "Success", Timestamp, TraceId)
{
    /// <summary>总页数；空集合为 0。</summary>
    public long TotalPage => Total == 0 ? 0 : ((Total - 1) / Limit) + 1;
}

/// <summary>HTTP 分页参数。业务查询仍由所属模块负责。</summary>
/// <param name="Page">页码，默认 1。</param>
/// <param name="Limit">每页大小，默认 50、最大 200。</param>
public sealed record ApiPageRequest(int Page = 1, int Limit = 50)
{
    /// <summary>请求参数是否合法。</summary>
    public bool IsValid => Page > 0 && Limit is > 0 and <= 200;
    /// <summary>条目偏移；使用 long 防止大页码溢出。</summary>
    public long Offset => ((long)Page - 1) * Limit;
}
