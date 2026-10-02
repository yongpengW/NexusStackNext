using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusStackNext.BuildingBlocks.Web;

// 只注册到宿主 HTTP 选项。数据库、事件、缓存和 JWT 保留各自的序列化契约。
internal sealed class HttpInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
        {
            return number;
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            return Parse(reader.GetString());
        }
        throw new JsonException("Expected a signed 64-bit integer or decimal integer string.");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));

    public override long ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString(CultureInfo.InvariantCulture));

    private static long Parse(string? value)
    {
        var digits = value.AsSpan();
        if (!digits.IsEmpty && digits[0] is '+' or '-')
        {
            digits = digits[1..];
        }
        if (!digits.IsEmpty && !digits.ContainsAnyExceptInRange('0', '9') &&
            long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }
        throw new JsonException("Expected a decimal integer string in the signed 64-bit range.");
    }
}
