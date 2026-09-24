using System.Net;
using System.Text;
using System.Text.Json;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>
/// Base dos streams que retransmitem uma resposta SSE do upstream convertendo os eventos no
/// caminho e registrando o uso. Read-through: acumula bytes brutos até haver um evento SSE
/// completo (terminado por linha em branco), entrega-o ao HandleEvent, que anexa a saída
/// convertida; Read serve essa saída. No fim (EOF/Dispose) completa o registro de uso e
/// descarta a resposta upstream. O cancelamento do cliente aborta o upstream, como no relay.
/// </summary>
internal abstract class ConvertingSseStream(
    Stream inner,
    string modelId,
    string? apiKeyId,
    UsageService usage,
    HttpResponseMessage upstreamResponse,
    CancellationToken clientCt) : Stream
{
    // Saída convertida pendente para o cliente. Append-only: escrita sempre no fim (Seek End),
    // leitura a partir do cursor _served — Position do MemoryStream não serve de cursor porque
    // Write o avançaria para o fim e esconderia os bytes ainda não entregues.
    private MemoryStream _output = new();
    private long _served;
    private readonly byte[] _readBuffer = new byte[8192];
    private string _pending = "";
    private bool _eof;
    private bool _completed;
    private bool _failed;

    /// <summary>Algo chegou do upstream (qualquer evento, inclusive ping).</summary>
    protected bool SawAnyEvent { get; private set; }

    protected long InputTokens { get; set; }
    protected long OutputTokens { get; set; }

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

        // Puxa do upstream até haver saída convertida pendente (ou EOF).
        while (_output.Length - _served == 0)
        {
            if (_eof)
            {
                break;
            }

            int read;
            try
            {
                read = inner.Read(_readBuffer, 0, _readBuffer.Length);
            }
            catch (Exception)
            {
                // Upstream fechou a conexão no meio do stream: trata como fim.
                read = 0;
            }

            if (read == 0)
            {
                _eof = true;
                // Último evento sem a linha em branco final: despacha o que sobrou.
                if (_pending.Length > 0)
                {
                    var segment = _pending;
                    _pending = "";
                    DispatchSegment(segment);
                }

                OnEof();
                break;
            }

            AppendRawChunk(Encoding.UTF8.GetString(_readBuffer, 0, read));
        }

        var available = (int)Math.Min(count, _output.Length - _served);
        if (available <= 0)
        {
            return 0;
        }

        _output.Seek(_served, SeekOrigin.Begin);
        _output.Read(buffer, offset, available);
        _served += available;

        // Compacta quando o cliente já consumiu a maior parte do buffer.
        if (_served >= 65536 && _served * 2 >= _output.Length)
        {
            var remaining = new byte[_output.Length - _served];
            _output.Seek(_served, SeekOrigin.Begin);
            _output.Read(remaining, 0, remaining.Length);
            _output = new MemoryStream(remaining, writable: true);
            _served = 0;
        }

        return available;
    }

    /// <summary>Acumula bytes brutos e despacha cada evento SSE completo que aparecer.</summary>
    private void AppendRawChunk(string text)
    {
        _pending += text.Replace("\r\n", "\n");

        while (true)
        {
            var boundary = _pending.IndexOf("\n\n", StringComparison.Ordinal);
            if (boundary < 0)
            {
                return;
            }

            var segment = _pending[..boundary];
            _pending = _pending[(boundary + 2)..];
            DispatchSegment(segment);
        }
    }

    private void DispatchSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return;
        }

        string? type = null;
        var dataLines = new List<string>();
        foreach (var line in segment.Split('\n'))
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                type = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
            }
            // id:/retry:/comentários: ignorados.
        }

        if (type is null && dataLines.Count == 0)
        {
            return;
        }

        SawAnyEvent = true;
        HandleEvent(type, string.Join("\n", dataLines), segment);
    }

    /// <summary>Converte um evento upstream completo e anexa o resultado à saída retransmitida.</summary>
    protected abstract void HandleEvent(string? eventType, string data, string rawSegment);

    /// <summary>Chamado uma vez quando o stream upstream termina; derivados podem emitir trailer aqui.</summary>
    protected virtual void OnEof()
    {
    }

    /// <summary>Anexa texto (já formatado como SSE) à saída pendente para o cliente.</summary>
    protected void AppendOutput(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _output.Seek(0, SeekOrigin.End); // sempre no fim: Position pode estar no cursor de leitura
        _output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Marca a requisição como falha (evento error do upstream).</summary>
    protected void MarkFailed() => _failed = true;

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_completed)
            {
                _completed = true;

                var cancelledBeforeEof = clientCt.IsCancellationRequested && !_eof;
                if (cancelledBeforeEof || _failed)
                {
                    // Stream interrompido ou erro do upstream: não conta como sucesso.
                    usage.Log(modelId, apiKeyId, 0, 0, success: false);
                }
                else if (SawAnyEvent || InputTokens > 0 || OutputTokens > 0)
                {
                    usage.Log(modelId, apiKeyId, InputTokens, OutputTokens, success: true);
                }
                // Nenhum evento chegou: nada a registrar.
            }

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

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Lê o campo "type" do payload JSON (presente nos eventos da Anthropic), com fallback
    /// para o nome do evento SSE.
    /// </summary>
    protected static string? EventKind(string? eventType, string data)
    {
        if (data.Length > 0 && data[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    return type.GetString();
                }
            }
            catch (JsonException)
            {
                // Payload malformado: usa o nome do evento, se houver.
            }
        }

        return eventType;
    }

    /// <summary>stop_reason Anthropic → finish_reason OpenAI.</summary>
    protected static string MapStopReasonToFinish(string? stopReason) => stopReason switch
    {
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        _ => "stop", // end_turn, stop_sequence e ausente.
    };

    /// <summary>finish_reason OpenAI → stop_reason Anthropic.</summary>
    protected static string MapFinishToStopReason(string? finishReason) => finishReason switch
    {
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        _ => "end_turn", // stop, content_filter e ausente.
    };
}

/// <summary>
/// Converte o stream SSE da Anthropic Messages em chunks OpenAI chat.completion (para clientes
/// OpenAI falando com um upstream Anthropic): message_start abre o chunk inicial de role,
/// text_delta vira delta.content, tool_use vira tool_calls (id/name no start, fragments de
/// arguments nos deltas) e message_delta/message_stop fecham com finish_reason + usage + [DONE].
/// Registra o uso a partir de input_tokens (message_start) e output_tokens (message_delta).
/// </summary>
internal sealed class AnthropicToOpenAiSseStream(
    Stream inner,
    string modelId,
    string? apiKeyId,
    UsageService usage,
    HttpResponseMessage upstreamResponse,
    CancellationToken clientCt,
    string fallbackModelId) : ConvertingSseStream(inner, modelId, apiKeyId, usage, upstreamResponse, clientCt)
{
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private string? _messageId;
    private string? _model;
    private bool _initialChunkEmitted;
    private bool _doneEmitted;
    private int _nextToolCallIndex;
    private readonly Dictionary<int, int> _toolCallIndex = new(); // índice do bloco Anthropic → índice tool_calls OpenAI

    protected override void HandleEvent(string? eventType, string data, string rawSegment)
    {
        if (data.Length == 0)
        {
            return;
        }

        JsonElement doc;
        try
        {
            using var parsed = JsonDocument.Parse(data);
            doc = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // Evento malformado: ignora sem derrubar o relay.
        }

        // Cada case vai entre chaves: os cases de switch compartilham escopo no C#.
        switch (EventKind(eventType, data))
        {
            case "message_start":
                {
                    if (doc.TryGetProperty("message", out var message))
                    {
                        _messageId = ReadString(message, "id");
                        _model = ReadString(message, "model");
                        if (message.TryGetProperty("usage", out var usageProp) &&
                            usageProp.ValueKind == JsonValueKind.Object &&
                            usageProp.TryGetProperty("input_tokens", out var input) &&
                            input.ValueKind == JsonValueKind.Number)
                        {
                            InputTokens = input.GetInt64();
                        }
                    }

                    EnsureInitialChunk();
                    break;
                }

            case "content_block_start":
                {
                    if (doc.TryGetProperty("content_block", out var block) && ReadString(block, "type") == "tool_use" &&
                        doc.TryGetProperty("index", out var indexProp) && indexProp.ValueKind == JsonValueKind.Number)
                    {
                        var blockIndex = indexProp.GetInt32();
                        _toolCallIndex[blockIndex] = _nextToolCallIndex++;

                        EnsureInitialChunk();
                        EmitChunk(delta: new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = _toolCallIndex[blockIndex],
                                    id = ReadString(block, "id") ?? $"call_{_toolCallIndex[blockIndex]}",
                                    type = "function",
                                    function = new { name = ReadString(block, "name") ?? "", arguments = "" },
                                },
                            },
                        }, finishReason: null);
                    }

                    // text/thinking: o chunk inicial já cobre a abertura.
                    break;
                }

            case "content_block_delta":
                {
                    if (!doc.TryGetProperty("delta", out var delta) || !doc.TryGetProperty("index", out var deltaIndexProp))
                    {
                        break;
                    }

                    var deltaType = ReadString(delta, "type");
                    if (deltaType == "text_delta")
                    {
                        var text = ReadString(delta, "text");
                        if (!string.IsNullOrEmpty(text))
                        {
                            EnsureInitialChunk();
                            EmitChunk(delta: new { content = text! }, finishReason: null);
                        }
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var blockIndex = deltaIndexProp.ValueKind == JsonValueKind.Number ? deltaIndexProp.GetInt32() : -1;
                        if (_toolCallIndex.TryGetValue(blockIndex, out var toolIndex))
                        {
                            var partial = ReadString(delta, "partial_json");
                            if (!string.IsNullOrEmpty(partial))
                            {
                                EmitChunk(delta: new
                                {
                                    tool_calls = new[] { new { index = toolIndex, function = new { arguments = partial! } } },
                                }, finishReason: null);
                            }
                        }
                    }
                    // thinking_delta/signature_delta: sem equivalente; descarta.
                    break;
                }

            case "message_delta":
                {
                    var stopReason = doc.TryGetProperty("delta", out var md) ? ReadString(md, "stop_reason") : null;
                    if (doc.TryGetProperty("usage", out var usageProp) &&
                        usageProp.ValueKind == JsonValueKind.Object &&
                        usageProp.TryGetProperty("output_tokens", out var output) &&
                        output.ValueKind == JsonValueKind.Number)
                    {
                        OutputTokens = output.GetInt64();
                    }

                    EnsureInitialChunk();
                    EmitChunk(delta: new { }, finishReason: MapStopReasonToFinish(stopReason));
                    break;
                }

            case "message_stop":
                EmitUsageAndDone();
                break;

            case "error":
                {
                    MarkFailed();
                    // Encaminha o erro no envelope do OpenAI para o cliente ver a causa.
                    var errorBody = doc.TryGetProperty("error", out var err) ? err.GetRawText() : data;
                    AppendOutput($"data: {{\"error\":{errorBody}}}\n\n");
                    break;
                }

            default:
                // ping e afins: ignora.
                break;
        }
    }

    protected override void OnEof()
    {
        // Stream cortado antes do message_stop: fecha com o que se tem.
        if (_initialChunkEmitted)
        {
            EmitUsageAndDone();
        }
    }

    private void EnsureInitialChunk()
    {
        if (_initialChunkEmitted)
        {
            return;
        }

        _initialChunkEmitted = true;
        // Sem message_start (stream malformado): id/model sintéticos.
        EmitChunk(delta: new { role = "assistant" }, finishReason: null);
    }

    private void EmitChunk(object delta, string? finishReason)
    {
        var chunk = new
        {
            id = "chatcmpl-" + (_messageId ?? "anthropic"),
            @object = "chat.completion.chunk",
            created = _created,
            model = _model ?? fallbackModelId,
            choices = new[]
            {
                new { index = 0, delta, finish_reason = finishReason },
            },
        };

        AppendOutput($"data: {JsonSerializer.Serialize(chunk)}\n\n");
    }

    private void EmitUsageAndDone()
    {
        if (_doneEmitted)
        {
            return;
        }

        _doneEmitted = true;

        var usageChunk = new
        {
            id = "chatcmpl-" + (_messageId ?? "anthropic"),
            @object = "chat.completion.chunk",
            created = _created,
            model = _model ?? fallbackModelId,
            choices = Array.Empty<object>(),
            usage = new
            {
                prompt_tokens = InputTokens,
                completion_tokens = OutputTokens,
                total_tokens = InputTokens + OutputTokens,
            },
        };

        AppendOutput($"data: {JsonSerializer.Serialize(usageChunk)}\n\n");
        AppendOutput("data: [DONE]\n\n");
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

/// <summary>
/// Converte o stream SSE OpenAI chat.completion em eventos Anthropic Messages (para clientes
/// Anthropic, ex.: Claude Code, falando com um upstream OpenAI-compatible): o primeiro delta
/// abre message_start, delta.content vira text_delta, tool_calls viram blocos tool_use com
/// fragments de input_json_delta e o final fecha com message_delta (stop_reason + usage) e
/// message_stop. O OpenAI só reporta prompt_tokens no último chunk, então a message_start sai
/// com input_tokens 0 (limitação conhecida); o uso registrado usa os valores reais do final.
/// </summary>
internal sealed class OpenAiToAnthropicSseStream(
    Stream inner,
    string modelId,
    string? apiKeyId,
    UsageService usage,
    HttpResponseMessage upstreamResponse,
    CancellationToken clientCt,
    string fallbackModelId) : ConvertingSseStream(inner, modelId, apiKeyId, usage, upstreamResponse, clientCt)
{
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private string? _messageId;
    private string? _model;
    private bool _messageStartEmitted;
    private bool _textBlockOpen;
    private bool _finalEmitted;
    private string? _pendingStopReason;
    private int _nextBlockIndex;
    private readonly Dictionary<int, int> _toolBlockIndex = new(); // índice tool_calls OpenAI → índice de bloco Anthropic

    protected override void HandleEvent(string? eventType, string data, string rawSegment)
    {
        if (data == "[DONE]")
        {
            EmitFinalEvents();
            return;
        }

        JsonElement doc;
        try
        {
            using var parsed = JsonDocument.Parse(data);
            doc = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // Chunk malformado: ignora sem derrubar o relay.
        }

        if (_messageId is null && ReadString(doc, "id") is { } id)
        {
            _messageId = id;
            _model = ReadString(doc, "model");
        }

        // Chunk de usage (choices vazio): chega no fim, depois do chunk de finish.
        if (!doc.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            if (doc.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                InputTokens = ReadLong(usageProp, "prompt_tokens");
                OutputTokens = ReadLong(usageProp, "completion_tokens");
            }

            return;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta))
            {
                continue;
            }

            var content = ReadString(delta, "content");
            if (!string.IsNullOrEmpty(content))
            {
                EnsureMessageStart();
                OpenTextBlockIfNeeded();
                EmitEvent("content_block_delta", new
                {
                    type = "content_block_delta",
                    index = 0,
                    delta = new { type = "text_delta", text = content! },
                });
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
                    if (!_toolBlockIndex.TryGetValue(index, out var blockIndex))
                    {
                        EnsureMessageStart();
                        blockIndex = _nextBlockIndex++;
                        _toolBlockIndex[index] = blockIndex;

                        var name = call.TryGetProperty("function", out var f) ? ReadString(f, "name") : null;
                        EmitEvent("content_block_start", new
                        {
                            type = "content_block_start",
                            index = blockIndex,
                            content_block = new
                            {
                                type = "tool_use",
                                id = call.TryGetProperty("id", out var cid) ? ReadString(call, "id") ?? $"toolu_{blockIndex}" : $"toolu_{blockIndex}",
                                name = name ?? "",
                                input = new { },
                            },
                        });
                    }

                    var arguments = call.TryGetProperty("function", out var fn) ? ReadString(fn, "arguments") : null;
                    if (!string.IsNullOrEmpty(arguments))
                    {
                        EmitEvent("content_block_delta", new
                        {
                            type = "content_block_delta",
                            index = blockIndex,
                            delta = new { type = "input_json_delta", partial_json = arguments! },
                        });
                    }
                }
            }

            var finishReason = ReadString(choice, "finish_reason");
            if (!string.IsNullOrEmpty(finishReason))
            {
                // Anthropic só carrega stop_reason no message_delta final: espera o usage/[DONE].
                _pendingStopReason = MapFinishToStopReason(finishReason);
            }
        }
    }

    protected override void OnEof() => EmitFinalEvents();

    private void EnsureMessageStart()
    {
        if (_messageStartEmitted)
        {
            return;
        }

        _messageStartEmitted = true;
        // O OpenAI não reporta input_tokens no início: sai 0 (o valor real vai no uso interno).
        EmitEvent("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = _messageId ?? $"msg_{fallbackModelId}",
                type = "message",
                role = "assistant",
                content = Array.Empty<object>(),
                model = _model ?? fallbackModelId,
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = 0, output_tokens = 0 },
            },
        });
    }

    private void OpenTextBlockIfNeeded()
    {
        if (_textBlockOpen)
        {
            return;
        }

        _textBlockOpen = true;
        EmitEvent("content_block_start", new
        {
            type = "content_block_start",
            index = 0,
            content_block = new { type = "text", text = "" },
        });
    }

    private void EmitFinalEvents()
    {
        if (_finalEmitted || !_messageStartEmitted)
        {
            return;
        }

        _finalEmitted = true;

        if (_textBlockOpen)
        {
            EmitEvent("content_block_stop", new { type = "content_block_stop", index = 0 });
        }

        foreach (var blockIndex in _toolBlockIndex.Values.OrderBy(i => i))
        {
            EmitEvent("content_block_stop", new { type = "content_block_stop", index = blockIndex });
        }

        EmitEvent("message_delta", new
        {
            type = "message_delta",
            delta = new
            {
                stop_reason = _pendingStopReason ?? "end_turn",
                stop_sequence = (string?)null,
            },
            usage = new { output_tokens = OutputTokens },
        });

        EmitEvent("message_stop", new { type = "message_stop" });
    }

    private void EmitEvent(string name, object payload)
    {
        AppendOutput($"event: {name}\ndata: {JsonSerializer.Serialize(payload)}\n\n");
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static long ReadLong(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt64();
        }

        return 0;
    }
}

/// <summary>
/// Passa o stream SSE da Anthropic para o cliente sem converter (client Anthropic → upstream
/// Anthropic), capturando apenas os tokens de uso: input_tokens no message_start e
/// output_tokens no message_delta. Pings e demais eventos transitam byte a byte, como exigido
/// pela detecção de stalling do Claude Code.
/// </summary>
internal sealed class AnthropicSsePassThroughStream(
    Stream inner,
    string modelId,
    string? apiKeyId,
    UsageService usage,
    HttpResponseMessage upstreamResponse,
    CancellationToken clientCt) : ConvertingSseStream(inner, modelId, apiKeyId, usage, upstreamResponse, clientCt)
{
    protected override void HandleEvent(string? eventType, string data, string rawSegment)
    {
        // Retransmite o segmento original (normalizado para \n).
        AppendOutput(rawSegment + "\n\n");

        if (data.Length == 0)
        {
            return;
        }

        JsonElement doc;
        try
        {
            using var parsed = JsonDocument.Parse(data);
            doc = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        switch (EventKind(eventType, data))
        {
            case "message_start":
                {
                    if (doc.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("usage", out var usageProp) &&
                        usageProp.ValueKind == JsonValueKind.Object)
                    {
                        InputTokens = ReadLong(usageProp, "input_tokens");
                    }

                    break;
                }

            case "message_delta":
                {
                    if (doc.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
                    {
                        OutputTokens = ReadLong(usageProp, "output_tokens");
                    }

                    break;
                }

            case "error":
                MarkFailed();
                break;
        }
    }

    private static long ReadLong(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt64();
        }

        return 0;
    }
}
