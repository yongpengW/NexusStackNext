using System.Globalization;
using System.Text.Json;
using Xunit;

namespace NexusStackNext.IntegrationSupport;

/// <summary>公开 HTTP 契约的严格读取断言，不接受会导致浏览器精度丢失的数字响应。</summary>
public static class HttpJson
{
    /// <summary>确认字段在线上是字符串，然后精确读取 Int64。</summary>
    /// <param name="element">HTTP JSON 字段。</param>
    /// <returns>精确的长整数。</returns>
    public static long ReadHttpInt64(this JsonElement element)
    {
        Assert.Equal(JsonValueKind.String, element.ValueKind);
        return long.Parse(element.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}
