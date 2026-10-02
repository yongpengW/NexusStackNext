using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>RFC ProblemDetails 错误，增加与成功信封一致的公共字段。</summary>
public sealed class ApiProblemDetails : ProblemDetails
{
    internal ApiProblemDetails(ProblemDetails source, HttpContext context, TimeProvider clock)
    {
        Status = source.Status ?? context.Response.StatusCode;
        Type = source.Type ?? "about:blank";
        Title = Status >= 500 ? ReasonPhrases.GetReasonPhrase(Code) : source.Title ?? ReasonPhrases.GetReasonPhrase(Code);
        Detail = Status >= 500 ? null : source.Detail;
        Instance = source.Instance ?? context.Request.Path.Value;
        Timestamp = clock.GetUtcNow().ToUnixTimeMilliseconds();
        TraceId = ApiResponses.TraceId(context);
        ErrorCode = source.Extensions.TryGetValue("errorCode", out var errorCode) && errorCode is string code
            ? code : $"http.{Code}";
        foreach (var extension in source.Extensions)
        {
            if (extension.Key is not ("success" or "code" or "message" or "data" or "timestamp" or "traceId" or "errorCode"))
            {
                Extensions[extension.Key] = extension.Value;
            }
        }
    }

    /// <summary>错误响应恒为 false。</summary>
    public bool Success { get; }
    /// <summary>实际 HTTP 状态码。</summary>
    public int Code => Status ?? StatusCodes.Status500InternalServerError;
    /// <summary>面向人的错误说明，与 title 一致。</summary>
    public string Message => Title ?? string.Empty;
    /// <summary>错误响应不携带成功数据。</summary>
    public object? Data { get; }
    /// <summary>响应时间，Unix 毫秒。</summary>
    public long Timestamp { get; }
    /// <summary>分布式调用追踪标识。</summary>
    public string TraceId { get; }
    /// <summary>稳定业务错误码；框架错误使用 http.状态码。</summary>
    public string ErrorCode { get; }
}
