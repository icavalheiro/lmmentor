using System.Net;
using System.Text.Json;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>Resultado da resolução de um modelo para o relay (inclui a chave validada).</summary>
public sealed record ResolvedModel(ApiKeyEntity Key, ModelEntity Model, ApiEndpointEntity Endpoint);

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

    public RelayService(
        IHttpClientFactory httpClientFactory,
        EndpointService endpoints,
        ApiKeyService keys,
        UsageService usage)
    {
        _httpClientFactory = httpClientFactory;
        _endpoints = endpoints;
        _keys = keys;
        _usage = usage;
    }

    /// <summary>Modelos habilitados, no formato da lista de modelos do OpenAI.</summary>
    public object ListModels()
    {
        var models = _endpoints.GetAllModelDtos()
            .Where(m => m.Enabled)
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
    public ResolvedModel Resolve(string apiKeyValue, string model)
    {
        var key = _keys.FindByKeyValue(apiKeyValue);
        if (key is null || key.RevokedAt is not null)
        {
            throw new RelayException(HttpStatusCode.Unauthorized, "Invalid API key.");
        }

        // Nome exposto (alias ou upstream), comparado sem diferenciar maiúsculas.
        var match = _endpoints.GetAllModels().FirstOrDefault(m =>
            m.Enabled && string.Equals(EndpointService.EffectiveName(m), model, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new RelayException(HttpStatusCode.NotFound, $"Model '{model}' not found.");
        }

        if (key.AllowedModelIds is { Count: > 0 } && !key.AllowedModelIds.Contains(match.Id))
        {
            throw new RelayException(HttpStatusCode.Forbidden, "This API key is not allowed to use the requested model.");
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
        var (isStreaming, bodyToForward) = PrepareRequestBody(requestBody);

        using var client = _httpClientFactory.CreateClient();
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

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

        if (!response.IsSuccessStatusCode)
        {
            // Erro do upstream: encaminha a mensagem e não conta como uso bem-sucedido.
            var errorBody = await response.Content.ReadAsStreamAsync(ct);
            _usage.Log(resolved.Model.Id, resolved.Key.Id, 0, 0, success: false);
            return new RelayResponse(response.StatusCode, contentType, errorBody);
        }

        if (isStreaming)
        {
            // Encaminha o SSE e captura o usage do último chunk ao final do stream.
            var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
            var relayed = new RelayingStream(upstreamStream, resolved.Model.Id, resolved.Key.Id, _usage);
            return new RelayResponse(HttpStatusCode.OK, contentType, relayed);
        }

        // Não-streaming: lê a resposta completa para extrair o usage antes de devolver.
        var responseBody = await response.Content.ReadAsByteArrayAsync(ct);
        LogUsageFromJson(responseBody, resolved.Model.Id, resolved.Key.Id, _usage);
        return new RelayResponse(HttpStatusCode.OK, contentType, new MemoryStream(responseBody));
    }

    /// <summary>
    /// Detecta streaming e, quando presente, injeta stream_options.include_usage para o
    /// upstream reportar tokens no último chunk. Retorna (isStreaming, corpo a enviar).
    /// </summary>
    private static (bool isStreaming, Stream body) PrepareRequestBody(Stream requestBody)
    {
        using var reader = new StreamReader(requestBody);
        var raw = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var isStreaming = root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

        if (!isStreaming)
        {
            return (false, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)));
        }

        // Reescreve o corpo substituindo/adicionando stream_options.include_usage.
        // Sem using: quem consome o stream (StreamContent) é responsável pelo descarte.
        var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "stream_options")
                {
                    continue;
                }

                writer.WritePropertyName(prop.Name);
                prop.Value.WriteTo(writer);
            }

            writer.WriteStartObject("stream_options");
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        output.Position = 0;
        return (true, output);
    }

    /// <summary>Extrai usage da resposta JSON completa e registra no log.</summary>
    private static void LogUsageFromJson(byte[] responseBody, string modelId, string apiKeyId, UsageService usage)
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
    };
}

/// <summary>
/// Stream que encaminha o SSE do upstream e, ao final (ou em falha), extrai o usage
/// do último chunk para registrar no log de uso.
/// </summary>
internal sealed class RelayingStream(Stream inner, string modelId, string apiKeyId, UsageService usage) : Stream
{
    private readonly List<string> _dataChunks = [];
    private bool _completed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                CaptureChunk(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            }

            return read;
        }
        catch (Exception)
        {
            // Upstream fechou a conexão no meio do stream: trata como fim para o cliente.
            return 0;
        }
    }

    private void CaptureChunk(string text)
    {
        // Mantém apenas as linhas "data: ..." do SSE para inspecionar o último chunk.
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                _dataChunks.Clear();
                _dataChunks.Add(line["data:".Length..].Trim());
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
        try
        {
            // Percorre de trás para frente em busca do chunk com usage (o último, no OpenAI).
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
            if (_dataChunks.Count > 0)
            {
                usage.Log(modelId, apiKeyId, 0, 0, success: true);
            }
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
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
