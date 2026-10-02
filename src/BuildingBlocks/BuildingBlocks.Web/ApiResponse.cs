namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>业务 JSON 成功响应；HTTP 状态与业务数据各有明确位置。</summary>
/// <typeparam name="T">业务数据。</typeparam>
/// <param name="Data">业务数据。</param>
/// <param name="Code">实际 HTTP 状态码。</param>
/// <param name="Message">面向人的说明。</param>
/// <param name="Timestamp">响应创建时间，Unix 毫秒。</param>
/// <param name="TraceId">本次分布式调用的追踪标识。</param>
public record ApiResponse<T>(T Data, int Code, string Message, long Timestamp, string TraceId)
{
    /// <summary>所有成功的 2xx JSON 结果均为 true。</summary>
    public bool Success => true;
}
