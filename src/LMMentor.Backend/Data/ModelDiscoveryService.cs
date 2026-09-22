using System.Net.Http;
using System.Text.Json;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>Modelo retornado pela descoberta em um endpoint upstream.</summary>
public sealed record DiscoveredModel(string UpstreamModelId, int? ContextSize);

/// <summary>
/// Descobre os modelos disponíveis em um endpoint chamando a API do provedor.
/// OpenAI-compatible (openai/groq/vllm/lmstudio/unsloth/custom) usa GET {url}/models;
/// Ollama usa GET {url}/api/tags.
/// </summary>
public sealed class ModelDiscoveryService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ModelDiscoveryService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Tenta descobrir os modelos do endpoint. Retorna (online, models); online é false
    /// quando a chamada falha ou o provedor não responde.
    /// </summary>
    public async Task<(bool online, List<DiscoveredModel> models)> DiscoverAsync(
        ApiEndpointEntity endpoint, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            if (string.Equals(endpoint.Type, "ollama", StringComparison.OrdinalIgnoreCase))
            {
                return await DiscoverOllamaAsync(client, endpoint, ct);
            }

            return await DiscoverOpenAiCompatibleAsync(client, endpoint, ct);
        }
        catch
        {
            // Timeout, erro de rede ou resposta inválida: o endpoint está inacessível.
            return (false, []);
        }
    }

    private static async Task<(bool online, List<DiscoveredModel> models)> DiscoverOpenAiCompatibleAsync(
        HttpClient client, ApiEndpointEntity endpoint, CancellationToken ct)
    {
        var url = TrimTrailingSlash(endpoint.Url) + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrEmpty(endpoint.AccessToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.AccessToken);
        }

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (false, []);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var models = new List<DiscoveredModel>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var upstreamId = id.GetString();
                if (string.IsNullOrWhiteSpace(upstreamId))
                {
                    continue;
                }

                models.Add(new DiscoveredModel(upstreamId, ReadContextSize(item)));
            }
        }

        return (true, models);
    }

    private static async Task<(bool online, List<DiscoveredModel> models)> DiscoverOllamaAsync(
        HttpClient client, ApiEndpointEntity endpoint, CancellationToken ct)
    {
        var url = TrimTrailingSlash(endpoint.Url) + "/api/tags";
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (false, []);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var models = new List<DiscoveredModel>();
        if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                // Ollama expõe "name" (ex.: llama3.1:70b) e "model" (ex.: llama3.1).
                var id = item.TryGetProperty("name", out var name) ? name.GetString()
                    : item.TryGetProperty("model", out var model) ? model.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                models.Add(new DiscoveredModel(id!, ReadContextSize(item)));
            }
        }

        return (true, models);
    }

    /// <summary>Extrai o tamanho de contexto quando o provedor o informa (max_context/context_length).</summary>
    private static int? ReadContextSize(JsonElement item)
    {
        foreach (var prop in new[] { "max_context", "context_length" })
        {
            if (item.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var size) && size > 0)
            {
                return size;
            }
        }

        return null;
    }

    private static string TrimTrailingSlash(string url) => url.TrimEnd('/');
}
