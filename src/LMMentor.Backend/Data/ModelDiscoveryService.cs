using System.Net.Http;
using System.Text.Json;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>Modelo retornado pela descoberta em um endpoint upstream.</summary>
public sealed record DiscoveredModel(string UpstreamModelId, int? ContextSize);

/// <summary>
/// Descobre os modelos disponíveis em um endpoint chamando a API do provedor.
/// OpenAI-compatible (openai/deepseek/groq/vllm/lmstudio/unsloth/custom) usa GET {url}/models;
/// Ollama usa GET {url}/api/tags.
/// </summary>
public sealed class ModelDiscoveryService
{
    // Provedores que servem o catálogo oficial de modelos, e não modelos hospedados localmente.
    private static readonly HashSet<string> RemoteProviderTypes =
        new(StringComparer.OrdinalIgnoreCase) { "openai", "deepseek", "groq", "custom" };

    // Campos usados pelos provedores OpenAI-compatible para informar o contexto do modelo.
    private static readonly string[] ContextFieldNames =
    [
        "context_length",
        "max_context",
        "max_context_length",
        "max_model_len",
        "context_window",
        "context_size",
    ];

    // Contexto dos modelos DeepSeek, usado quando o provedor não o informa na listagem
    // (GET /models da DeepSeek devolve apenas id/created/owned_by).
    // Fonte: https://api-docs.deepseek.com/quick_start/pricing
    private static readonly Dictionary<string, int> KnownContextSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-flash"] = 1_000_000,
        ["deepseek-v4-flash"] = 1_000_000,
        ["deepseek-v4-flash-vision-exp"] = 1_000_000,
        ["deepseek-v4-pro"] = 1_000_000,
    };

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

                models.Add(new DiscoveredModel(upstreamId, ResolveContextSize(endpoint, upstreamId, item)));
            }
        }

        return (true, models);
    }

    /// <summary>
    /// Contexto do modelo: prioriza o valor reportado pelo provedor e, quando ele não o
    /// informa, completa com os valores conhecidos dos modelos oficiais da DeepSeek.
    /// </summary>
    private static int? ResolveContextSize(ApiEndpointEntity endpoint, string upstreamId, JsonElement item)
    {
        var reported = ReadContextSize(item);
        if (reported is not null)
        {
            return reported;
        }

        // O fallback só vale em provedores que servem o catálogo oficial: em servidores
        // locais (Ollama, vLLM, LM Studio) o mesmo nome pode ter contexto diferente.
        var usesOfficialCatalog = RemoteProviderTypes.Contains(endpoint.Type);
        if (usesOfficialCatalog && KnownContextSizes.TryGetValue(upstreamId, out var known))
        {
            return known;
        }

        return null;
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

    /// <summary>Extrai o tamanho de contexto quando o provedor o informa em algum dos campos conhecidos.</summary>
    private static int? ReadContextSize(JsonElement item)
    {
        foreach (var field in ContextFieldNames)
        {
            if (item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var size) && size > 0)
            {
                return size;
            }
        }

        return null;
    }

    private static string TrimTrailingSlash(string url) => url.TrimEnd('/');
}
