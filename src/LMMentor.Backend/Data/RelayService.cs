using System.Net;
using System.Text.Json;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>Resultado da resolução de um modelo para o relay (inclui a chave validada).</summary>
public sealed record ResolvedModel(ApiKeyEntity? Key, ModelEntity Model, ApiEndpointEntity Endpoint);

/// <summary>Erro do relay com status HTTP associado (chave inválida, modelo inexistente, etc.).</summary>
public sealed class RelayException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>Resposta a ser devolvida ao cliente pelo relay.</summary>
public sealed class RelayResponse
{
    public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

    public string ContentType { get; init; } = "application/json";

    /// <summary>Corpo pronto (não-streaming) ou stream a encaminhar linha a linha (SSE).</summary>
    public Stream Body { get; init; } = Stream.Null;

    public RelayResponse(HttpStatusCode statusCode, string contentType, Stream body)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
    }

    public static RelayResponse Json(HttpStatusCode statusCode, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return new RelayResponse(statusCode, "application/json", new MemoryStream(json));
    }
}

/// <summary>
/// Relay OpenAI-compatible: valida a chave do cliente, resolve o modelo pelo nome
/// exposto, encaminha a requisição para o endpoint upstream (streaming ou não) e
/// registra o uso no log que alimenta o dashboard.
/// </summary>
public sealed class RelayService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EndpointService _endpoints;
    private readonly ApiKeyService _keys;
    private readonly UsageService _usage;
    private readonly ApplicationSettingsService _settings;

    public RelayService(
        IHttpClientFactory httpClientFactory,
        EndpointService endpoints,
        ApiKeyService keys,
        UsageService usage,
        ApplicationSettingsService settings)
    {
        _httpClientFactory = httpClientFactory;
        _endpoints = endpoints;
        _keys = keys;
        _usage = usage;
        _settings = settings;
    }

    /// <summary>Modelos habilitados e permitidos pela chave, no formato da lista do OpenAI.</summary>
    public object ListModels(string? apiKeyValue)
    {
        var key = ResolveKey(apiKeyValue);
        var bypassesAuthentication = _settings.OllamaCompatibilityEnabled;
        var models = _endpoints.GetAllModelDtos()
            .Where(m => m.Enabled && (bypassesAuthentication || IsModelAllowed(key!, m.Id)))
            .Select(m => new
            {
                id = EndpointService.EffectiveName(ToEntity(m)),
                @object = "model",
                created = 0,
                owned_by = "lmmentor",
                context_length = m.ContextSize,
            });

        return new { @object = "list", data = models };
    }

    /// <summary>Resolve o modelo pelo nome exposto (ou id upstream) e valida a chave do cliente.</summary>
    public ResolvedModel Resolve(string? apiKeyValue, string model)
    {
        var key = ResolveKey(apiKeyValue);
        var bypassesAuthentication = _settings.OllamaCompatibilityEnabled;

        // Aceita o nome exposto e o id original, para clientes configurados antes de um alias.
        var match = _endpoints.GetAllModels().FirstOrDefault(m =>
            m.Enabled &&
            (string.Equals(EndpointService.EffectiveName(m), model, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(m.UpstreamModelId, model, StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            throw new RelayException(HttpStatusCode.NotFound, $"Model '{model}' not found.");
        }

        if (!bypassesAuthentication && !IsModelAllowed(key!, match.Id))
        {
            throw new RelayException(HttpStatusCode.Forbidden, "This API key is not allowed to use the requested model.");
        }

        // Restrição recorrente de horário (ex.: rush hour de um provedor): responde 503 em vez de encaminhar.
        if (ModelAvailability.IsBlockedAt(match.BlockedWindows, DateTime.Now))
        {
            throw new RelayException(HttpStatusCode.ServiceUnavailable, $"Model '{model}' is temporarily unavailable due to a scheduled restriction window.");
        }

        var endpoint = _endpoints.GetById(match.EndpointId);
        if (endpoint is null)
        {
            throw new RelayException(HttpStatusCode.NotFound, "Endpoint for the requested model no longer exists.");
        }

        return new ResolvedModel(key, match, endpoint);
    }

    /// <summary>Encaminha uma requisição de chat completion para o upstream e registra o uso.</summary>
    public async Task<RelayResponse> RelayChatAsync(ResolvedModel resolved, Stream requestBody, CancellationToken ct)
    {
        // Upstream Anthropic: converte OpenAI → Anthropic Messages nos dois sentidos.
        if (IsAnthropic(resolved.Endpoint))
        {
            return await RelayOpenAiToAnthropicAsync(resolved, requestBody, ct);
        }

        var (isStreaming, bodyToForward) = PrepareRequestBody(requestBody, resolved.Model.UpstreamModelId);

        var client = _httpClientFactory.CreateClient();
        // Streaming exige leitura a partir dos headers; o timeout cobre apenas a conexão.
        client.Timeout = TimeSpan.FromSeconds(30);

        var url = TrimTrailingSlash(resolved.Endpoint.Url) + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StreamContent(bodyToForward),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (!string.IsNullOrEmpty(resolved.Endpoint.AccessToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolved.Endpoint.AccessToken);
        }

        HttpResponseMessage? response = null;
        var handedOffToStream = false;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

            if (!response.IsSuccessStatusCode)
            {
                // Erro do upstream: preserva a mensagem e não conta como uso bem-sucedido.
                var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
                _usage.Log(resolved.Model.Id, resolved.Key?.Id, 0, 0, success: false);
                return new RelayResponse(response.StatusCode, contentType, new MemoryStream(errorBody));
            }

            if (isStreaming)
            {
                // O response permanece aberto: o RelayingStream assume a posse e o descarta.
                var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
                var relayed = new RelayingStream(upstreamStream, resolved.Model.Id, resolved.Key?.Id, _usage, response, ct);
                handedOffToStream = true;
                return new RelayResponse(HttpStatusCode.OK, contentType, relayed);
            }

            // Não-streaming: lê a resposta completa para extrair o usage antes de devolver.
            var responseBody = await response.Content.ReadAsByteArrayAsync(ct);
            LogUsageFromJson(responseBody, resolved.Model.Id, resolved.Key?.Id, _usage);
            return new RelayResponse(HttpStatusCode.OK, contentType, new MemoryStream(responseBody));
        }
        finally
        {
            if (!handedOffToStream)
            {
                response?.Dispose();
            }
        }
    }

    /// <summary>Modelos habilitados e permitidos pela chave, no formato da Anthropic Models API
    /// (usado na descoberta de modelos do Claude Code via gateway).</summary>
    public object ListAnthropicModels(string? apiKeyValue)
    {
        var key = ResolveKey(apiKeyValue);
        var bypassesAuthentication = _settings.OllamaCompatibilityEnabled;
        var data = _endpoints.GetAllModelDtos()
            .Where(m => m.Enabled && (bypassesAuthentication || IsModelAllowed(key!, m.Id)))
            .Select(m => new
            {
                id = EndpointService.EffectiveName(ToEntity(m)),
                type = "model",
                display_name = string.IsNullOrEmpty(m.DisplayName) ? null : m.DisplayName,
                max_input_tokens = m.ContextSize,
                max_tokens = m.MaxOutputTokens,
            });

        return new { @object = "list", data };
    }

    /// <summary>
    /// Encaminha um pedido Anthropic Messages (ex.: Claude Code) para o upstream e registra o uso.
    /// Upstream Anthropic: pass-through com reescrita do model e headers do cliente encaminhados.
    /// Upstream OpenAI-compatible: converte nos dois sentidos (pedido, resposta e stream SSE).
    /// </summary>
    public async Task<RelayResponse> RelayMessagesAsync(
        ResolvedModel resolved, Stream requestBody, string? clientAnthropicBeta, CancellationToken ct)
    {
        if (IsAnthropic(resolved.Endpoint))
        {
            return await RelayAnthropicPassthroughAsync(resolved, requestBody, clientAnthropicBeta, ct);
        }

        return await RelayAnthropicToOpenAiAsync(resolved, requestBody, ct);
    }

    private static bool IsAnthropic(ApiEndpointEntity endpoint) =>
        string.Equals(endpoint.Type, "anthropic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cliente OpenAI → upstream Anthropic: converte o pedido para a Messages API e a resposta
    /// (ou stream SSE) de volta para o formato chat.completions.
    /// </summary>
    private async Task<RelayResponse> RelayOpenAiToAnthropicAsync(ResolvedModel resolved, Stream requestBody, CancellationToken ct)
    {
        var (isStreaming, requestBytes) = PrepareAnthropicRequest(requestBody, resolved);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var url = AnthropicAdapter.V1Root(resolved.Endpoint.Url) + "/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(requestBytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        AddAnthropicAuthHeaders(request, resolved.Endpoint);
        if (isStreaming)
        {
            request.Headers.Accept.ParseAdd("text/event-stream");
        }

        HttpResponseMessage? response = null;
        var handedOffToStream = false;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

            if (!response.IsSuccessStatusCode)
            {
                // Erro do upstream: preserva a mensagem (formato Anthropic) e não conta como sucesso.
                var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
                _usage.Log(resolved.Model.Id, resolved.Key?.Id, 0, 0, success: false);
                return new RelayResponse(response.StatusCode, contentType, new MemoryStream(errorBody));
            }

            if (isStreaming)
            {
                // O response permanece aberto: o stream conversor assume a posse e o descarta.
                var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
                var relayed = new AnthropicToOpenAiSseStream(
                    upstreamStream, resolved.Model.Id, resolved.Key?.Id, _usage, response, ct, resolved.Model.UpstreamModelId);
                handedOffToStream = true;
                return new RelayResponse(HttpStatusCode.OK, "text/event-stream", relayed);
            }

            var responseBody = await response.Content.ReadAsByteArrayAsync(ct);
            LogAnthropicUsageFromJson(responseBody, resolved.Model.Id, resolved.Key?.Id, _usage);
            var converted = AnthropicAdapter.ToOpenAiCompletion(
                JsonSerializer.Deserialize<JsonElement>(responseBody), resolved.Model.UpstreamModelId);
            return new RelayResponse(HttpStatusCode.OK, "application/json", new MemoryStream(converted));
        }
        finally
        {
            if (!handedOffToStream)
            {
                response?.Dispose();
            }
        }
    }

    /// <summary>
    /// Cliente Anthropic → upstream Anthropic: pass-through do corpo (reescrita apenas do model,
    /// como no relay OpenAI), encaminhando os headers anthropic-version/anthropic-beta do cliente.
    /// </summary>
    private async Task<RelayResponse> RelayAnthropicPassthroughAsync(
        ResolvedModel resolved, Stream requestBody, string? clientAnthropicBeta, CancellationToken ct)
    {
        var (isStreaming, bodyToForward) = PreparePassthroughRequestBody(requestBody, resolved.Model.UpstreamModelId);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var url = AnthropicAdapter.V1Root(resolved.Endpoint.Url) + "/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StreamContent(bodyToForward),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        AddAnthropicAuthHeaders(request, resolved.Endpoint);

        // O Claude Code envia betas pareados com campos do corpo: os dois precisam chegar ao upstream.
        if (!string.IsNullOrWhiteSpace(clientAnthropicBeta))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", clientAnthropicBeta);
        }

        if (isStreaming)
        {
            request.Headers.Accept.ParseAdd("text/event-stream");
        }

        HttpResponseMessage? response = null;
        var handedOffToStream = false;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
                _usage.Log(resolved.Model.Id, resolved.Key?.Id, 0, 0, success: false);
                return new RelayResponse(
                    response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType ?? "application/json",
                    new MemoryStream(errorBody));
            }

            if (isStreaming)
            {
                // Pass-through com captura de uso: os bytes (incluindo pings) transitam intactos.
                var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
                var relayed = new AnthropicSsePassThroughStream(
                    upstreamStream, resolved.Model.Id, resolved.Key?.Id, _usage, response, ct);
                handedOffToStream = true;
                return new RelayResponse(HttpStatusCode.OK, "text/event-stream", relayed);
            }

            var responseBody = await response.Content.ReadAsByteArrayAsync(ct);
            LogAnthropicUsageFromJson(responseBody, resolved.Model.Id, resolved.Key?.Id, _usage);
            // A resposta já está no formato Anthropic: devolve como veio.
            return new RelayResponse(HttpStatusCode.OK, "application/json", new MemoryStream(responseBody));
        }
        finally
        {
            if (!handedOffToStream)
            {
                response?.Dispose();
            }
        }
    }

    /// <summary>
    /// Cliente Anthropic → upstream OpenAI-compatible: converte o pedido para chat.completions e
    /// a resposta (ou stream SSE) de volta para os eventos da Messages API.
    /// </summary>
    private async Task<RelayResponse> RelayAnthropicToOpenAiAsync(ResolvedModel resolved, Stream requestBody, CancellationToken ct)
    {
        var (isStreaming, requestBytes) = PrepareOpenAiRequest(requestBody, resolved.Model.UpstreamModelId);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var url = TrimTrailingSlash(resolved.Endpoint.Url) + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(requestBytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (!string.IsNullOrEmpty(resolved.Endpoint.AccessToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolved.Endpoint.AccessToken);
        }

        HttpResponseMessage? response = null;
        var handedOffToStream = false;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Erro do upstream em formato OpenAI: o cliente Anthropic vê o envelope dele.
                var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
                _usage.Log(resolved.Model.Id, resolved.Key?.Id, 0, 0, success: false);
                return new RelayResponse(
                    response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType ?? "application/json",
                    new MemoryStream(errorBody));
            }

            if (isStreaming)
            {
                var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
                var relayed = new OpenAiToAnthropicSseStream(
                    upstreamStream, resolved.Model.Id, resolved.Key?.Id, _usage, response, ct, resolved.Model.UpstreamModelId);
                handedOffToStream = true;
                return new RelayResponse(HttpStatusCode.OK, "text/event-stream", relayed);
            }

            var responseBody = await response.Content.ReadAsByteArrayAsync(ct);
            LogUsageFromJson(responseBody, resolved.Model.Id, resolved.Key?.Id, _usage);
            var converted = AnthropicAdapter.ToAnthropicMessage(
                JsonSerializer.Deserialize<JsonElement>(responseBody), resolved.Model.UpstreamModelId);
            return new RelayResponse(HttpStatusCode.OK, "application/json", new MemoryStream(converted));
        }
        finally
        {
            if (!handedOffToStream)
            {
                response?.Dispose();
            }
        }
    }

    /// <summary>Headers de autenticação/versionamento da API Anthropic para um upstream.</summary>
    private static void AddAnthropicAuthHeaders(HttpRequestMessage request, ApiEndpointEntity endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.AccessToken))
        {
            // A Anthropic usa x-api-key (não Bearer) na API pública.
            request.Headers.TryAddWithoutValidation("x-api-key", endpoint.AccessToken);
        }

        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicAdapter.ApiVersion);
    }

    /// <summary>
    /// Lê o pedido OpenAI, detecta streaming e o converte para a Anthropic Messages API.
    /// O max_tokens padrão usa o teto conhecido do modelo (descoberto via /v1/models).
    /// </summary>
    private (bool isStreaming, byte[] body) PrepareAnthropicRequest(Stream requestBody, ResolvedModel resolved)
    {
        using var reader = new StreamReader(requestBody);
        var raw = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var isStreaming = root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

        return (isStreaming, AnthropicAdapter.BuildMessagesRequest(root, resolved.Model.UpstreamModelId, resolved.Model.MaxOutputTokens));
    }

    /// <summary>
    /// Lê o pedido Anthropic, detecta streaming e o converte para OpenAI chat-completions
    /// (incluindo stream_options.include_usage nos streams).
    /// </summary>
    private static (bool isStreaming, byte[] body) PrepareOpenAiRequest(Stream requestBody, string upstreamModelId)
    {
        using var reader = new StreamReader(requestBody);
        var raw = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var isStreaming = root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

        return (isStreaming, AnthropicAdapter.BuildChatCompletionRequest(root, upstreamModelId));
    }

    /// <summary>
    /// Pass-through do corpo Anthropic: reescreve o model para o id upstream e preserva todos os
    /// demais campos byte a byte (system com cache_control, thinking, tools etc.).
    /// </summary>
    private static (bool isStreaming, Stream body) PreparePassthroughRequestBody(Stream requestBody, string upstreamModelId)
    {
        using var reader = new StreamReader(requestBody);
        var raw = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var isStreaming = root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

        // Sem using: quem consome o stream (StreamContent) é responsável pelo descarte.
        var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "model")
                {
                    writer.WriteString("model", upstreamModelId);
                    continue;
                }

                writer.WritePropertyName(prop.Name);
                prop.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        output.Position = 0;
        return (isStreaming, output);
    }

    /// <summary>Extrai usage (input/output tokens) de uma resposta Anthropic e registra no log.</summary>
    private static void LogAnthropicUsageFromJson(byte[] responseBody, string modelId, string? apiKeyId, UsageService usage)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(responseBody);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("usage", out var usageProp))
            {
                var input = usageProp.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0;
                var output = usageProp.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0;
                usage.Log(modelId, apiKeyId, input, output, success: true);
            }
            else
            {
                usage.Log(modelId, apiKeyId, 0, 0, success: true);
            }
        }
        catch
        {
            // Uso não reportado (resposta malformada): registra com zeros.
            usage.Log(modelId, apiKeyId, 0, 0, success: true);
        }
    }

    private ApiKeyEntity? ResolveKey(string? apiKeyValue)
    {
        if (_settings.OllamaCompatibilityEnabled)
        {
            return FindOptionalActiveKey(apiKeyValue);
        }

        if (string.IsNullOrWhiteSpace(apiKeyValue))
        {
            throw new RelayException(HttpStatusCode.Unauthorized, "Invalid API key.");
        }

        var key = _keys.FindByKeyValue(apiKeyValue);
        if (key is null || key.RevokedAt is not null)
        {
            throw new RelayException(HttpStatusCode.Unauthorized, "Invalid API key.");
        }

        return key;
    }

    private ApiKeyEntity? FindOptionalActiveKey(string? apiKeyValue)
    {
        if (string.IsNullOrWhiteSpace(apiKeyValue))
        {
            return null;
        }

        var key = _keys.FindByKeyValue(apiKeyValue);
        return key is { RevokedAt: null } ? key : null;
    }

    private static bool IsModelAllowed(ApiKeyEntity key, string modelId) =>
        key.AllowedModelIds is not { Count: > 0 } || key.AllowedModelIds.Contains(modelId);

    /// <summary>
    /// Detecta streaming e, quando presente, injeta stream_options.include_usage para o
    /// upstream reportar tokens no último chunk. Também substitui o alias público pelo
    /// identificador real aceito pelo upstream. Retorna (isStreaming, corpo a enviar).
    /// </summary>
    private static (bool isStreaming, Stream body) PrepareRequestBody(Stream requestBody, string upstreamModelId)
    {
        using var reader = new StreamReader(requestBody);
        var raw = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var isStreaming = root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

        // Reescreve o corpo para usar o id upstream e, nos streams, incluir usage.
        // Sem using: quem consome o stream (StreamContent) é responsável pelo descarte.
        var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "model")
                {
                    writer.WriteString("model", upstreamModelId);
                    continue;
                }

                if (isStreaming && prop.Name == "stream_options")
                {
                    continue;
                }

                writer.WritePropertyName(prop.Name);
                prop.Value.WriteTo(writer);
            }

            if (isStreaming)
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        output.Position = 0;
        return (isStreaming, output);
    }

    /// <summary>Extrai usage da resposta JSON completa e registra no log.</summary>
    private static void LogUsageFromJson(byte[] responseBody, string modelId, string? apiKeyId, UsageService usage)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(responseBody);
            if (json.TryGetProperty("usage", out var usageProp))
            {
                var prompt = usageProp.TryGetProperty("prompt_tokens", out var p) ? p.GetInt64() : 0;
                var completion = usageProp.TryGetProperty("completion_tokens", out var c) ? c.GetInt64() : 0;
                usage.Log(modelId, apiKeyId, prompt, completion, success: true);
            }
            else
            {
                usage.Log(modelId, apiKeyId, 0, 0, success: true);
            }
        }
        catch
        {
            // Resposta sem usage legível: registra apenas o sucesso da requisição.
            usage.Log(modelId, apiKeyId, 0, 0, success: true);
        }
    }

    private static string TrimTrailingSlash(string url) => url.TrimEnd('/');

    private static ModelEntity ToEntity(ModelDto dto) => new()
    {
        Id = dto.Id,
        EndpointId = dto.EndpointId,
        UpstreamModelId = dto.UpstreamModelId,
        DisplayName = dto.DisplayName,
        ContextSize = dto.ContextSize,
        Enabled = dto.Enabled,
        BlockedWindows = dto.BlockedWindows.ToList(),
    };
}

/// <summary>
/// Stream que encaminha o SSE do upstream e, ao final (ou em falha), extrai o usage
/// dos últimos chunks para registrar no log de uso.
/// </summary>
internal sealed class RelayingStream(
    Stream inner,
    string modelId,
    string? apiKeyId,
    UsageService usage,
    HttpResponseMessage upstreamResponse,
    CancellationToken clientCt) : Stream
{
    // Janela de linhas SSE mantidas em memória para localizar o chunk com "usage".
    // Os chunks não carregam contagem: os totais da requisição vêm num único objeto
    // "usage" no penúltimo chunk (o OpenAI envia [DONE] depois), então 16 linhas bastam.
    private const int MaxTrackedChunks = 16;

    private readonly List<string> _dataChunks = [];
    private string _pendingLine = "";
    private bool _completed;
    private bool _eofSeen;

    // Cancelamento do cliente descarta a resposta upstream: fecha o socket (HTTP/1.1) ou
    // envia RST_STREAM (HTTP/2), fazendo o provider interromper a geração imediatamente.
    private readonly CancellationTokenRegistration _abortRegistration = clientCt.CanBeCanceled
        ? clientCt.Register(static s => AbortUpstream((HttpResponseMessage)s!), upstreamResponse)
        : default;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (clientCt.IsCancellationRequested)
        {
            // Cliente cancelou: a resposta upstream já foi descartada; encerra para o cliente.
            return 0;
        }

        try
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                CaptureChunk(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            }
            else
            {
                _eofSeen = true;
            }

            return read;
        }
        catch (Exception)
        {
            // Upstream fechou a conexão no meio do stream: trata como fim para o cliente.
            return 0;
        }
    }

    private static void AbortUpstream(HttpResponseMessage response)
    {
        try
        {
            response.Dispose();
        }
        catch
        {
            // Descarte pode falhar se a conexão já caiu: nada mais a fazer.
        }
    }

    private void CaptureChunk(string text)
    {
        // Uma linha SSE pode ser dividida entre leituras de rede: acumula o fragmento parcial.
        _pendingLine += text;

        var lines = _pendingLine.Split('\n');
        _pendingLine = lines[^1];

        foreach (var line in lines)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            _dataChunks.Add(payload);
            if (_dataChunks.Count > MaxTrackedChunks)
            {
                _dataChunks.RemoveAt(0);
            }
        }
    }

    private void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;

        // Stream interrompido pelo cancelamento do cliente antes do EOF: não conta como sucesso.
        var cancelledBeforeEof = clientCt.IsCancellationRequested && !_eofSeen;
        if (cancelledBeforeEof)
        {
            usage.Log(modelId, apiKeyId, 0, 0, success: false);
            return;
        }

        if (_dataChunks.Count == 0)
        {
            // Stream vazio: nada a registrar.
            return;
        }

        try
        {
            // Percorre de trás para frente em busca do chunk com usage (chega antes do [DONE], no OpenAI).
            for (var i = _dataChunks.Count - 1; i >= 0; i--)
            {
                var chunk = _dataChunks[i];
                if (chunk == "[DONE]")
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(chunk);
                if (doc.RootElement.TryGetProperty("usage", out var usageProp) &&
                    usageProp.ValueKind == JsonValueKind.Object)
                {
                    var prompt = usageProp.TryGetProperty("prompt_tokens", out var p) ? p.GetInt64() : 0;
                    var completion = usageProp.TryGetProperty("completion_tokens", out var c) ? c.GetInt64() : 0;
                    usage.Log(modelId, apiKeyId, prompt, completion, success: true);
                    return;
                }
            }

            // Stream com dados mas sem usage reportado: registra o sucesso sem tokens.
            usage.Log(modelId, apiKeyId, 0, 0, success: true);
        }
        catch
        {
            // Chunk malformado no final: ignora para não derrubar o relay.
        }
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Complete();
            _abortRegistration.Dispose();
            try
            {
                inner.Dispose();
            }
            finally
            {
                upstreamResponse.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
