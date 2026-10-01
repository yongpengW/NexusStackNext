using System.Net.Http.Json;
using System.Text.Json;

namespace NexusStackNext.HostIntegration.Tests;

internal static class ApiTestResponse
{
    public static async Task<JsonElement> ReadApiDataAsync(this HttpContent content)
    {
        var response = await content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.GetProperty("success").GetBoolean());
        return response.GetProperty("data");
    }
}
