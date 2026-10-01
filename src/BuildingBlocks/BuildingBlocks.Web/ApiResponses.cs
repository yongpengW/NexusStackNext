using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>端点的 JSON 结果适配器。由端点决定状态，不在网关改写下游正文。</summary>
/// <param name="accessor">当前 HTTP 请求。</param>
/// <param name="clock">时间来源。</param>
public sealed class ApiResponses(IHttpContextAccessor accessor, TimeProvider clock)
{
    /// <summary>读取或执行成功，返回 200。</summary>
    /// <typeparam name="T">业务数据。</typeparam>
    /// <param name="data">业务数据。</param>
    /// <returns>具有准确 OpenAPI 元数据的成功结果。</returns>
    public Ok<ApiResponse<T>> Ok<T>(T data) => TypedResults.Ok(Create(data, StatusCodes.Status200OK));

    /// <summary>已接收的工作，返回 202。</summary>
    /// <typeparam name="T">业务数据。</typeparam>
    /// <param name="data">业务数据。</param>
    /// <returns>已接收结果。</returns>
    public Accepted<ApiResponse<T>> Accepted<T>(T data) =>
        TypedResults.Accepted((string?)null, Create(data, StatusCodes.Status202Accepted));

    /// <summary>返回已由所属模块查询好的分页结果。</summary>
    /// <typeparam name="T">条目类型。</typeparam>
    /// <param name="items">本页条目。</param>
    /// <param name="total">符合查询条件的总条数。</param>
    /// <param name="request">有效分页参数。</param>
    /// <returns>分页成功结果。</returns>
    public Ok<ApiPage<T>> Page<T>(IReadOnlyList<T> items, long total, ApiPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (!request.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        var metadata = Create(items, StatusCodes.Status200OK);
        return TypedResults.Ok(new ApiPage<T>(items, total, request.Page, request.Limit, metadata.Timestamp, metadata.TraceId));
    }

    /// <summary>创建成功，保留资源位置与 201 状态。</summary>
    /// <typeparam name="T">业务数据。</typeparam>
    /// <param name="location">资源地址。</param>
    /// <param name="data">业务数据。</param>
    /// <returns>具有准确 OpenAPI 元数据的创建结果。</returns>
    public Created<ApiResponse<T>> Created<T>(string location, T data) =>
        TypedResults.Created(location, Create(data, StatusCodes.Status201Created));

    private ApiResponse<T> Create<T>(T data, int status) => new(
        data, status, "Success", clock.GetUtcNow().ToUnixTimeMilliseconds(),
        TraceId(accessor.HttpContext ?? throw new InvalidOperationException("JSON 结果只能在 HTTP 请求内创建。")));

    internal static string TraceId(HttpContext context) => Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
}
