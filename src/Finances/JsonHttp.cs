using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Finances;

public sealed class ApiException(int status, string url, string body)
    : Exception($"HTTP {status} from {url}: {(body.Length > 300 ? body[..300] : body)}")
{
    public int Status { get; } = status;
}

/// <summary>A thin JSON layer over HttpClient; the tests swap the handler underneath.</summary>
public sealed class JsonHttp(HttpClient client)
{
    public async Task<JsonElement> GetAsync(string url, IEnumerable<KeyValuePair<string, string>>? query = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (query is not null)
        {
            var parts = query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}").ToList();
            if (parts.Count > 0) url = $"{url}?{string.Join("&", parts)}";
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, headers);
    }

    public async Task<JsonElement> PostJsonAsync(string url, object body, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return await SendAsync(request, headers);
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        request.Headers.UserAgent.ParseAdd("openvitals-finances/1 (+https://github.com/OpenVitals-MTU/finances)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(key, value);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new ApiException((int)response.StatusCode, request.RequestUri!.GetLeftPart(UriPartial.Path), text);
        using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return document.RootElement.Clone();
    }
}

public static class JsonExtensions
{
    public static string Str(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    public static string? StrOrNull(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static long Long(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    public static bool Bool(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static JsonElement? Obj(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
