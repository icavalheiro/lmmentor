using System.Net.Http;
using System.Text.Json;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>Modelo retornado pela descoberta em um endpoint upstream.</summary>
public sealed record DiscoveredModel(string UpstreamModelId, int? ContextSize);

/// <summary>
/// Descobre os modelos disponíveis em um endpoint chamando a API do provedor.
/// OpenAI-compatible (openai/deepseek/groq/vllm/lmstudio/llamacpp/unsloth/custom) usa GET {url}/models;
/// Ollama usa GET {url}/api/tags. Cada provedor informa o contexto de um jeito — vLLM em
/// max_model_len, Groq em context_window, LM Studio no REST nativo (/api/v1/models),
/// llama-server e Unsloth Studio no /props, Ollama no POST /api/show — e o valor é reavaliado
/// a cada descoberta, pois o modelo pode ser recarregado com outro tamanho.
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
            if (IsType(endpoint, "ollama"))
            {
                return await DiscoverOllamaAsync(client, endpoint, ct);
            }

            var (online, models) = await DiscoverOpenAiCompatibleAsync(client, endpoint, ct);
            if (!online || models.Count == 0)
            {
                return (online, models);
            }

            return (online, await ApplyProviderContextAsync(client, endpoint, models, ct));
        }
        catch
        {
            // Timeout, erro de rede ou resposta inválida: o endpoint está inacessível.
            return (false, []);
        }
    }

    private static bool IsType(ApiEndpointEntity endpoint, string type) =>
        string.Equals(endpoint.Type, type, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Complementa o contexto que o GET /models não informa, conforme o tipo do provedor:
    /// o LM Studio expõe o dado no REST nativo e llama-server/Unsloth no /props.
    /// </summary>
    private static async Task<List<DiscoveredModel>> ApplyProviderContextAsync(
        HttpClient client, ApiEndpointEntity endpoint, List<DiscoveredModel> models, CancellationToken ct)
    {
        if (IsType(endpoint, "lmstudio"))
        {
            return await ApplyLmStudioContextAsync(client, endpoint, models, ct);
        }

        return UsesLlamaServerProps(endpoint)
            ? await ApplyLlamaServerPropsAsync(client, endpoint, models, ct)
            : models;
    }

    /// <summary>Provedores cujo contexto vem do /props de um llama-server (direto ou embutido).</summary>
    private static bool UsesLlamaServerProps(ApiEndpointEntity endpoint) =>
        IsType(endpoint, "llamacpp") || IsType(endpoint, "unsloth");

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
        var url = RootUrl(endpoint.Url) + "/api/tags";
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

        // O /api/tags não traz o contexto; ele vem de /api/show, um modelo por vez. A sonda
        // roda a cada descoberta, pois o contexto efetivo pode mudar entre updates.
        var withContext = new List<DiscoveredModel>(models.Count);
        foreach (var model in models)
        {
            var contextSize = model.ContextSize
                ?? await ReadOllamaContextAsync(client, endpoint, model.UpstreamModelId, ct);
            withContext.Add(model with { ContextSize = contextSize });
        }

        return (true, withContext);
    }

    /// <summary>
    /// Sonda o contexto do llama-server, usado diretamente (tipo llama.cpp) ou embutido no
    /// Unsloth Studio: GET /props traz o contexto efetivo do modelo residente em
    /// default_generation_settings.n_ctx e o modelo em model_path.
    /// </summary>
    private static async Task<List<DiscoveredModel>> ApplyLlamaServerPropsAsync(
        HttpClient client, ApiEndpointEntity endpoint, List<DiscoveredModel> models, CancellationToken ct)
    {
        if (models.Count == 0)
        {
            return models;
        }

        var (contextSize, residentModelId) = await ProbeLlamaServerPropsAsync(client, endpoint, ct);
        var target = ResolveLlamaServerTarget(models, contextSize, residentModelId);
        if (target is null)
        {
            return models;
        }

        return models
            .Select(m => m == target ? m with { ContextSize = contextSize } : m)
            .ToList();
    }

    /// <summary>
    /// Modelo ao qual o contexto do /props pertence: o que casa com o modelo residente
    /// (model_path) ou, sem correspondência, o único modelo do endpoint — llama-server e
    /// Unsloth Studio servem um modelo por vez. Sem alvo claro, o valor não é aplicado para
    /// não atribuir contexto errado.
    /// </summary>
    private static DiscoveredModel? ResolveLlamaServerTarget(
        List<DiscoveredModel> models, int? contextSize, string? residentModelId)
    {
        if (contextSize is null)
        {
            return null;
        }

        if (residentModelId is not null)
        {
            var matched = models.FirstOrDefault(m =>
                string.Equals(m.UpstreamModelId, residentModelId, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched;
            }
        }

        return models.Count == 1 ? models[0] : null;
    }

    /// <summary>Lê /props de um llama-server. Retorna (contexto, modelo residente).</summary>
    private static async Task<(int? contextSize, string? residentModelId)> ProbeLlamaServerPropsAsync(
        HttpClient client, ApiEndpointEntity endpoint, CancellationToken ct)
    {
        // A base URL pode vir com ou sem /v1: o llama-server serve /props e o Studio também /v1/props.
        var root = RootUrl(endpoint.Url);
        var candidates = new[] { root + "/props", root + "/v1/props" };

        foreach (var candidate in candidates)
        {
            var props = await GetJsonAsync(client, endpoint, candidate, ct);
            if (props is null)
            {
                continue;
            }

            var (contextSize, residentModelId) = ReadLlamaServerProps(props.Value);
            if (contextSize is not null)
            {
                return (contextSize, residentModelId);
            }
        }

        return (null, null);
    }

    private static (int? contextSize, string? residentModelId) ReadLlamaServerProps(JsonElement props)
    {
        var contextSize = ReadLlamaServerContextSize(props);
        var residentModelId = ReadString(props, "model_path");
        return (contextSize, residentModelId);
    }

    /// <summary>Contexto efetivo do llama-server: default_generation_settings.n_ctx.</summary>
    private static int? ReadLlamaServerContextSize(JsonElement props)
    {
        var hasSettings = props.TryGetProperty("default_generation_settings", out var settings)
            && settings.ValueKind == JsonValueKind.Object;

        return hasSettings ? ReadPositiveInt(settings, "n_ctx") : null;
    }

    /// <summary>
    /// Contexto dos modelos do LM Studio. O /v1/models (OpenAI-compatible) devolve só o id;
    /// o REST nativo traz o contexto efetivo da instância carregada em
    /// loaded_instances[].config.context_length e, como limite do modelo, max_context_length.
    /// </summary>
    private static async Task<List<DiscoveredModel>> ApplyLmStudioContextAsync(
        HttpClient client, ApiEndpointEntity endpoint, List<DiscoveredModel> models, CancellationToken ct)
    {
        var root = RootUrl(endpoint.Url);
        foreach (var path in new[] { "/api/v1/models", "/api/v0/models" })
        {
            var document = await GetJsonAsync(client, endpoint, root + path, ct);
            if (document is null)
            {
                continue;
            }

            var contexts = ReadLmStudioContexts(document.Value);
            if (contexts.Count == 0)
            {
                continue;
            }

            return models
                .Select(m => m.ContextSize is null && contexts.TryGetValue(m.UpstreamModelId, out var size)
                    ? m with { ContextSize = size }
                    : m)
                .ToList();
        }

        return models;
    }

    /// <summary>Mapa id exposto no /v1/models → contexto, lido do REST nativo do LM Studio.</summary>
    private static Dictionary<string, int> ReadLmStudioContexts(JsonElement document)
    {
        var contexts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hasModels = document.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array;
        if (!hasModels)
        {
            return contexts;
        }

        foreach (var model in models.EnumerateArray())
        {
            var contextSize = ReadLoadedInstanceContext(model) ?? ReadPositiveInt(model, "max_context_length");
            if (contextSize is null)
            {
                continue;
            }

            foreach (var id in ReadLmStudioIds(model))
            {
                contexts[id] = contextSize.Value;
            }
        }

        return contexts;
    }

    /// <summary>Contexto efetivo da instância carregada; modelo não carregado não tem uma.</summary>
    private static int? ReadLoadedInstanceContext(JsonElement model)
    {
        var hasInstances = model.TryGetProperty("loaded_instances", out var instances)
            && instances.ValueKind == JsonValueKind.Array;
        if (!hasInstances)
        {
            return null;
        }

        foreach (var instance in instances.EnumerateArray())
        {
            var hasConfig = instance.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object;
            if (!hasConfig)
            {
                continue;
            }

            var contextSize = ReadPositiveInt(config, "context_length");
            if (contextSize is not null)
            {
                return contextSize;
            }
        }

        return null;
    }

    /// <summary>Ids pelos quais o LM Studio pode expor o mesmo modelo no /v1/models.</summary>
    private static IEnumerable<string> ReadLmStudioIds(JsonElement model)
    {
        foreach (var field in new[] { "key", "selected_variant" })
        {
            var id = ReadString(model, field);
            if (id is not null)
            {
                yield return id;
            }
        }

        var hasInstances = model.TryGetProperty("loaded_instances", out var instances)
            && instances.ValueKind == JsonValueKind.Array;
        if (!hasInstances)
        {
            yield break;
        }

        foreach (var instance in instances.EnumerateArray())
        {
            var id = ReadString(instance, "id");
            if (id is not null)
            {
                yield return id;
            }
        }
    }

    /// <summary>
    /// Contexto de um modelo Ollama via POST /api/show: lê model_info (ex.:
    /// "llama.context_length") e, quando ausente, o parâmetro num_ctx.
    /// </summary>
    private static async Task<int?> ReadOllamaContextAsync(
        HttpClient client, ApiEndpointEntity endpoint, string modelName, CancellationToken ct)
    {
        var url = RootUrl(endpoint.Url) + "/api/show";
        try
        {
            var payload = JsonSerializer.Serialize(new { model = modelName });
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };

            if (!string.IsNullOrEmpty(endpoint.AccessToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.AccessToken);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return ReadOllamaShowContext(doc.RootElement);
        }
        catch
        {
            // Sonda opcional: falha em um modelo não interrompe a descoberta dos demais.
            return null;
        }
    }

    private static int? ReadOllamaShowContext(JsonElement show)
    {
        var hasModelInfo = show.TryGetProperty("model_info", out var modelInfo)
            && modelInfo.ValueKind == JsonValueKind.Object;

        if (hasModelInfo)
        {
            foreach (var prop in modelInfo.EnumerateObject())
            {
                var isContextField = prop.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prop.Name, "context_length", StringComparison.OrdinalIgnoreCase);

                if (isContextField && prop.Value.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetInt32(out var size) && size > 0)
                {
                    return size;
                }
            }
        }

        var hasParameters = show.TryGetProperty("parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.String;

        return hasParameters ? ReadNumCtxParameter(parameters.GetString()) : null;
    }

    /// <summary>Lê "num_ctx N" das linhas de parameters do /api/show.</summary>
    private static int? ReadNumCtxParameter(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return null;
        }

        foreach (var line in parameters.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var isNumCtx = parts.Length >= 2 && string.Equals(parts[0], "num_ctx", StringComparison.OrdinalIgnoreCase);
            if (isNumCtx && int.TryParse(parts[1], out var size) && size > 0)
            {
                return size;
            }
        }

        return null;
    }

    /// <summary>GET que devolve JSON, com o token do endpoint; null em qualquer falha.</summary>
    private static async Task<JsonElement?> GetJsonAsync(
        HttpClient client, ApiEndpointEntity endpoint, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(endpoint.AccessToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.AccessToken);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            // Clone: o JsonDocument é descartado ao sair do método.
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
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

    /// <summary>URL raiz do servidor: sem a barra final e sem o sufixo /v1 da base configurada.</summary>
    private static string RootUrl(string url)
    {
        var root = TrimTrailingSlash(url);
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root[..^3] : root;
    }

    /// <summary>Lê uma string não vazia da propriedade informada.</summary>
    private static string? ReadString(JsonElement element, string propertyName)
    {
        var hasValue = element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String;
        var text = hasValue ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Lê um inteiro positivo da propriedade informada.</summary>
    private static int? ReadPositiveInt(JsonElement element, string propertyName)
    {
        var hasValue = element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number;
        return hasValue && value.TryGetInt32(out var size) && size > 0 ? size : null;
    }
}
