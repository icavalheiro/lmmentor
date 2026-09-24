using System.Text;
using System.Text.Json;

namespace LMMentor.Backend.Data;

/// <summary>
/// Adaptador entre o protocolo OpenAI chat-completions e a Anthropic Messages API.
/// Quatro mapeamentos: pedido e resposta em cada direção. Os campos que não têm
/// equivalente no outro lado (ex.: cache_control, thinking) são descartados na
/// conversão; os streams SSE têm conversores próprios (SseRelayStreams.cs).
/// </summary>
public static class AnthropicAdapter
{
    /// <summary>Versão da API Anthropic enviada em todo request ao upstream.</summary>
    public const string ApiVersion = "2023-06-01";

    /// <summary>Limite de stop_sequences aceito pela API Anthropic.</summary>
    private const int MaxStopSequences = 4;

    /// <summary>max_tokens quando o cliente não informa e o teto do modelo é desconhecido.</summary>
    private const int DefaultMaxTokens = 8192;

    /// <summary>Formato aceito pela API Anthropic para metadata.user_id.</summary>
    private static readonly System.Text.RegularExpressions.Regex UserPattern =
        new("^[A-Za-z0-9._-]{1,256}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Raiz da API Anthropic: a base configurada garantindo o sufixo /v1 (aceita tanto
    /// https://api.anthropic.com quanto https://api.anthropic.com/v1).
    /// </summary>
    public static string V1Root(string url)
    {
        var root = url.TrimEnd('/');
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/v1";
    }

    // ==================== OpenAI request → Anthropic request ====================

    /// <summary>
    /// Converte um pedido OpenAI chat-completions em um pedido Anthropic Messages.
    /// System messages viram o parâmetro top-level "system"; tool_calls/tool results do
    /// histórico viram blocos tool_use/tool_result; max_tokens é obrigatório na Anthropic,
    /// então a falta de valor no cliente cai para o teto conhecido do modelo ou um padrão.
    /// </summary>
    public static byte[] BuildMessagesRequest(JsonElement openAiRequest, string upstreamModelId, int? knownMaxOutputTokens)
    {
        var root = openAiRequest;

        // System: campo top-level (não-padrão, usado por alguns SDKs) + messages de role system.
        var systemParts = new List<string>();
        if (root.TryGetProperty("system", out var topSystem) && topSystem.ValueKind == JsonValueKind.String)
        {
            AddSystemText(systemParts, topSystem.GetString());
        }

        var output = new List<OutputMessage>();
        var pendingToolResults = new List<(string Id, JsonElement Content)>();

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                var role = ReadString(message, "role") ?? string.Empty;

                if (role == "system")
                {
                    AddSystemText(systemParts, FlattenContentToText(ReadContent(message)));
                    continue;
                }

                // Respostas a tool_calls (role "tool") viram blocos tool_result em uma única
                // message user: a Anthropic exige que resultados de ferramentas cheguem assim.
                if (role == "tool")
                {
                    pendingToolResults.Add((ReadString(message, "tool_call_id") ?? string.Empty, ReadContent(message)));
                    continue;
                }

                if (role is not ("user" or "assistant"))
                {
                    continue;
                }

                FlushToolResults(pendingToolResults, output);

                var parts = new List<ContentPart>();
                MapOpenAiContent(parts, ReadContent(message));

                // tool_calls do assistente viram blocos tool_use na mesma message.
                if (role == "assistant" &&
                    message.TryGetProperty("tool_calls", out var toolCalls) &&
                    toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        var function = call.TryGetProperty("function", out var f) ? f : default;
                        var name = ReadString(function, "name");
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        parts.Add(new ToolUsePart(ReadString(call, "id") ?? $"call_{parts.Count}", name!, ParseToolArguments(ReadString(function, "arguments"))));
                    }
                }

                // A Anthropic exige alternância user/assistant: funde messages consecutivas do mesmo role.
                if (output.Count > 0 && output[^1].Role == role)
                {
                    output[^1].Parts.AddRange(parts);
                }
                else if (parts.Count > 0)
                {
                    output.Add(new OutputMessage { Role = role, Parts = parts });
                }
            }

            FlushToolResults(pendingToolResults, output);
        }

        var maxTokens = ReadPositiveInt(root, "max_tokens")
            ?? ReadPositiveInt(root, "max_completion_tokens")
            ?? knownMaxOutputTokens
            ?? DefaultMaxTokens;

        var stopSequences = MapStopSequences(root);
        var tools = MapOpenAiTools(root);
        var toolChoice = MapOpenAiToolChoice(root);

        var outputBytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outputBytes))
        {
            writer.WriteStartObject();
            writer.WriteString("model", upstreamModelId);
            writer.WriteNumber("max_tokens", maxTokens);

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in output)
            {
                WriteAnthropicMessage(writer, message);
            }
            writer.WriteEndArray();

            var system = string.Join("\n\n", systemParts.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (!string.IsNullOrWhiteSpace(system))
            {
                writer.WriteString("system", system);
            }

            CopyNumber(root, writer, "temperature");
            CopyNumber(root, writer, "top_p");

            if (stopSequences is { Count: > 0 })
            {
                writer.WritePropertyName("stop_sequences");
                writer.WriteStartArray();
                foreach (var sequence in stopSequences)
                {
                    writer.WriteStringValue(sequence);
                }
                writer.WriteEndArray();
            }

            if (tools is { Count: > 0 })
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    if (!string.IsNullOrEmpty(tool.Description))
                    {
                        writer.WriteString("description", tool.Description);
                    }

                    writer.WritePropertyName("input_schema");
                    tool.InputSchema.WriteTo(writer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            if (toolChoice is not null)
            {
                writer.WritePropertyName("tool_choice");
                toolChoice.Value.WriteTo(writer);
            }

            // metadata.user_id: a Anthropic restringe o formato; só encaminha se válido.
            var userId = ReadString(root, "user");
            if (userId is not null && UserPattern.IsMatch(userId))
            {
                writer.WriteStartObject("metadata");
                writer.WriteString("user_id", userId);
                writer.WriteEndObject();
            }

            if (root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            {
                writer.WriteBoolean("stream", true);
            }

            writer.WriteEndObject();
        }

        return outputBytes.ToArray();
    }

    // ==================== Anthropic request → OpenAI request ====================

    /// <summary>
    /// Converte um pedido Anthropic Messages em um pedido OpenAI chat-completions.
    /// O "system" (string ou array de blocos) vira uma message system no início do histórico;
    /// tool_use do histórico vira tool_calls e tool_result vira messages role "tool";
    /// thinking/cache_control e demais campos sem equivalente são descartados. Em streams,
    /// injeta stream_options.include_usage para o upstream reportar tokens no último chunk.
    /// </summary>
    public static byte[] BuildChatCompletionRequest(JsonElement anthropicRequest, string upstreamModelId)
    {
        var root = anthropicRequest;

        var systemParts = new List<string>();
        if (root.TryGetProperty("system", out var systemProp))
        {
            if (systemProp.ValueKind == JsonValueKind.String)
            {
                AddSystemText(systemParts, systemProp.GetString());
            }
            else if (systemProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in systemProp.EnumerateArray())
                {
                    if (ReadString(block, "type") == "text")
                    {
                        AddSystemText(systemParts, ReadString(block, "text"));
                    }
                }
            }
        }

        var output = new List<OutputMessage>();
        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                var role = ReadString(message, "role");
                if (role is not ("user" or "assistant"))
                {
                    continue;
                }

                var parts = new List<ContentPart>();
                var toolResults = new List<(string Id, JsonElement Content)>();

                var content = ReadContent(message);
                if (content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(new TextPart(text!));
                    }
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        switch (ReadString(block, "type") ?? string.Empty)
                        {
                            case "text":
                                var text = ReadString(block, "text");
                                if (!string.IsNullOrEmpty(text))
                                {
                                    parts.Add(new TextPart(text!));
                                }
                                break;

                            case "image":
                                MapAnthropicImage(parts, block);
                                break;

                            // thinking/redacted_thinking/document: sem equivalente OpenAI; descarta.
                            case "tool_use" when role == "assistant":
                                var name = ReadString(block, "name");
                                if (string.IsNullOrEmpty(name))
                                {
                                    continue;
                                }

                                parts.Add(new ToolUsePart(ReadString(block, "id") ?? $"call_{parts.Count}", name!, block.TryGetProperty("input", out var input) ? input : default));
                                break;

                            case "tool_result":
                                toolResults.Add((ReadString(block, "tool_use_id") ?? string.Empty, ReadToolResultContent(block)));
                                break;
                        }
                    }
                }

                // A Anthropic já alterna roles. Um user message pode misturar texto e
                // tool_results: o texto fica na message user e cada resultado vira uma
                // message "tool" subsequente (formato OpenAI).
                if (parts.Count > 0)
                {
                    output.Add(new OutputMessage { Role = role, Parts = parts });
                }

                foreach (var (id, toolContent) in toolResults)
                {
                    var isEmpty = toolContent.ValueKind == JsonValueKind.Undefined ||
                        (toolContent.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(toolContent.GetString())) ||
                        (toolContent.ValueKind == JsonValueKind.Array && toolContent.GetArrayLength() == 0);

                    var effective = isEmpty ? JsonSerializer.SerializeToElement("(empty)") : toolContent;
                    output.Add(new OutputMessage { Role = "tool", Parts = [new ToolResultPart(id, effective)] });
                }
            }
        }

        // System vai na frente do histórico (convenção OpenAI).
        var system = string.Join("\n\n", systemParts.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(system))
        {
            output.Insert(0, new OutputMessage { Role = "system", Parts = [new TextPart(system)] });
        }

        var isStreaming = root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;

        var outputBytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outputBytes))
        {
            writer.WriteStartObject();
            writer.WriteString("model", upstreamModelId);

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in output)
            {
                WriteOpenAiMessage(writer, message);
            }
            writer.WriteEndArray();

            CopyNumber(root, writer, "max_tokens");
            CopyNumber(root, writer, "temperature");
            CopyNumber(root, writer, "top_p");

            if (root.TryGetProperty("stop_sequences", out var stops) && stops.ValueKind == JsonValueKind.Array)
            {
                var sequences = new List<string>();
                foreach (var stop in stops.EnumerateArray())
                {
                    if (stop.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(stop.GetString()))
                    {
                        sequences.Add(stop.GetString()!);
                    }
                }

                if (sequences.Count > 0)
                {
                    writer.WritePropertyName("stop");
                    if (sequences.Count == 1)
                    {
                        writer.WriteStringValue(sequences[0]);
                    }
                    else
                    {
                        writer.WriteStartArray();
                        foreach (var sequence in sequences)
                        {
                            writer.WriteStringValue(sequence);
                        }

                        writer.WriteEndArray();
                    }
                }
            }

            var tools = MapAnthropicTools(root);
            if (tools is { Count: > 0 })
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name);
                    if (!string.IsNullOrEmpty(tool.Description))
                    {
                        writer.WriteString("description", tool.Description);
                    }

                    writer.WritePropertyName("parameters");
                    tool.InputSchema.WriteTo(writer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            var toolChoice = MapAnthropicToolChoice(root);
            if (toolChoice is not null)
            {
                writer.WritePropertyName("tool_choice");
                toolChoice.Value.WriteTo(writer);
            }

            if (isStreaming)
            {
                writer.WriteBoolean("stream", true);
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return outputBytes.ToArray();
    }

    // ==================== Anthropic response → OpenAI completion ====================

    /// <summary>
    /// Converte uma resposta (não-streaming) da Anthropic Messages em um chat.completion
    /// OpenAI: blocos de texto viram content, tool_use vira tool_calls e usage input/output
    /// vira prompt/completion tokens.
    /// </summary>
    public static byte[] ToOpenAiCompletion(JsonElement anthropicResponse, string upstreamModelId)
    {
        var root = anthropicResponse;

        var text = new StringBuilder();
        var toolCalls = new List<JsonElement>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (ReadString(block, "type"))
                {
                    case "text":
                        var textValue = ReadString(block, "text");
                        if (!string.IsNullOrEmpty(textValue))
                        {
                            text.Append(textValue);
                        }

                        break;

                    case "tool_use":
                        var name = ReadString(block, "name") ?? string.Empty;
                        var arguments = block.TryGetProperty("input", out var input) && input.ValueKind != JsonValueKind.Null
                            ? CompactJson(input)
                            : "{}";
                        toolCalls.Add(JsonSerializer.SerializeToElement(new
                        {
                            id = ReadString(block, "id") ?? $"call_{toolCalls.Count}",
                            type = "function",
                            function = new { name, arguments },
                        }));
                        break;
                }
            }
        }

        var hasText = text.Length > 0;
        var stopReason = ReadString(root, "stop_reason");
        var finishReason = stopReason switch
        {
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            // end_turn, stop_sequence e ausente: para o OpenAI é um stop.
            _ => "stop",
        };

        var usage = root.TryGetProperty("usage", out var u) ? u : default;
        var inputTokens = ReadPositiveInt(usage, "input_tokens") ?? 0;
        var outputTokens = ReadPositiveInt(usage, "output_tokens") ?? 0;

        var outputBytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outputBytes))
        {
            writer.WriteStartObject();
            writer.WriteString("id", "chatcmpl-" + (ReadString(root, "id") ?? upstreamModelId));
            writer.WriteString("object", "chat.completion");
            writer.WriteNumber("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.WriteString("model", ReadString(root, "model") ?? upstreamModelId);

            writer.WritePropertyName("choices");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("message");
            writer.WriteString("role", "assistant");

            if (hasText)
            {
                writer.WriteString("content", text.ToString());
            }
            else
            {
                writer.WriteNull("content");
            }

            if (toolCalls.Count > 0)
            {
                writer.WritePropertyName("tool_calls");
                writer.WriteStartArray();
                foreach (var call in toolCalls)
                {
                    call.WriteTo(writer);
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject(); // message
            writer.WriteString("finish_reason", finishReason);
            writer.WriteEndObject(); // choice
            writer.WriteEndArray();

            WriteOpenAiUsage(writer, usage, inputTokens, outputTokens);

            writer.WriteEndObject();
        }

        return outputBytes.ToArray();
    }

    // ==================== OpenAI completion → Anthropic message ====================

    /// <summary>
    /// Converte um chat.completion OpenAI (não-streaming) em uma resposta Anthropic Messages:
    /// content vira blocos de texto, tool_calls viram blocos tool_use e usage prompt/completion
    /// vira input/output tokens.
    /// </summary>
    public static byte[] ToAnthropicMessage(JsonElement openAiResponse, string upstreamModelId)
    {
        var root = openAiResponse;

        var blocks = new List<ContentPart>();
        var finishReason = string.Empty;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var message))
                {
                    continue;
                }

                finishReason = ReadString(choice, "finish_reason") ?? string.Empty;

                var content = ReadContent(message);
                if (content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        blocks.Add(new TextPart(text!));
                    }
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        switch (ReadString(part, "type"))
                        {
                            case "text":
                                var text = ReadString(part, "text");
                                if (!string.IsNullOrEmpty(text))
                                {
                                    blocks.Add(new TextPart(text!));
                                }
                                break;

                            case "image_url":
                                MapOpenAiImage(blocks, part);
                                break;
                        }
                    }
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        var function = call.TryGetProperty("function", out var f) ? f : default;
                        var name = ReadString(function, "name");
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        blocks.Add(new ToolUsePart(ReadString(call, "id") ?? $"toolu_{blocks.Count}", name!, ParseToolArguments(ReadString(function, "arguments"))));
                    }
                }
            }
        }

        var stopReason = finishReason switch
        {
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            // stop, content_filter e ausente: para a Anthropic é end_turn.
            _ => "end_turn",
        };

        var usage = root.TryGetProperty("usage", out var u) ? u : default;
        var inputTokens = ReadPositiveInt(usage, "prompt_tokens") ?? 0;
        var outputTokens = ReadPositiveInt(usage, "completion_tokens") ?? 0;
        var cachedTokens = ReadPositiveInt(usage, "prompt_tokens_details.cached_tokens") ?? 0;

        var outputBytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outputBytes))
        {
            writer.WriteStartObject();
            writer.WriteString("id", ReadString(root, "id") ?? $"msg_{upstreamModelId}");
            writer.WriteString("type", "message");
            writer.WriteString("role", "assistant");
            writer.WriteString("model", ReadString(root, "model") ?? upstreamModelId);

            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var block in blocks)
            {
                WriteAnthropicBlock(writer, block);
            }
            writer.WriteEndArray();

            writer.WriteString("stop_reason", stopReason);
            writer.WriteNull("stop_sequence");

            writer.WriteStartObject("usage");
            writer.WriteNumber("input_tokens", inputTokens);
            writer.WriteNumber("output_tokens", outputTokens);
            if (cachedTokens > 0)
            {
                writer.WriteNumber("cache_read_input_tokens", cachedTokens);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return outputBytes.ToArray();
    }

    // ==================== Helpers compartilhados ====================

    /// <summary>Parte de conteúdo intermediária, independente do protocolo.</summary>
    private abstract record ContentPart;

    private sealed record TextPart(string Text) : ContentPart;

    /// <summary>Imagem como URI (data:… ou http…) — o serializador decide base64 vs url.</summary>
    private sealed record ImagePart(string Url) : ContentPart;

    private sealed record ToolUsePart(string Id, string Name, JsonElement Input) : ContentPart;

    private sealed record ToolResultPart(string ToolCallId, JsonElement Content) : ContentPart;

    /// <summary>Message de saída em construção (role + partes ainda não serializadas).</summary>
    private sealed class OutputMessage
    {
        public required string Role { get; init; }

        public required List<ContentPart> Parts { get; init; }
    }

    private sealed record AnthropicTool(string Name, string? Description, JsonElement InputSchema);

    private static void AddSystemText(List<string> parts, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(text!);
        }
    }

    /// <summary>Lê a propriedade content (string, array ou ausente/null).</summary>
    private static JsonElement ReadContent(JsonElement message) =>
        message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null
            ? content
            : default;

    /// <summary>Conteúdo de um tool_result: string, array de blocos ou ausente.</summary>
    private static JsonElement ReadToolResultContent(JsonElement block) =>
        block.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null
            ? content
            : default;

    /// <summary>Textualiza um conteúdo OpenAI (string ou array de partes) para o system.</summary>
    private static string? FlattenContentToText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (ReadString(part, "type") == "text")
            {
                var text = ReadString(part, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    texts.Add(text!);
                }
            }
        }

        return texts.Count > 0 ? string.Join("\n\n", texts) : null;
    }

    /// <summary>Conteúdo OpenAI (string/array) em partes intermediárias.</summary>
    private static void MapOpenAiContent(List<ContentPart> parts, JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(new TextPart(text!));
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in content.EnumerateArray())
        {
            switch (ReadString(part, "type"))
            {
                case "text":
                    var text = ReadString(part, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(new TextPart(text!));
                    }
                    break;

                case "image_url":
                    MapOpenAiImage(parts, part);
                    break;

                // input_audio e afins: sem equivalente na Anthropic Messages; descarta.
            }
        }
    }

    /// <summary>image_url OpenAI → ImagePart (data URI ou URL http).</summary>
    private static void MapOpenAiImage(List<ContentPart> parts, JsonElement part)
    {
        var url = part.TryGetProperty("image_url", out var imageUrl)
            ? (imageUrl.ValueKind == JsonValueKind.String
                ? imageUrl.GetString()
                : imageUrl.TryGetProperty("url", out var u) ? u.GetString() : null)
            : null;

        if (!string.IsNullOrEmpty(url))
        {
            parts.Add(new ImagePart(url!));
        }
    }

    /// <summary>Bloco image da Anthropic → ImagePart (base64 vira data URI, url passa direto).</summary>
    private static void MapAnthropicImage(List<ContentPart> parts, JsonElement block)
    {
        if (!block.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (ReadString(source, "type"))
        {
            case "base64":
                var mediaType = ReadString(source, "media_type") ?? "image/png";
                var data = ReadString(source, "data");
                if (!string.IsNullOrEmpty(data))
                {
                    parts.Add(new ImagePart($"data:{mediaType};base64,{data}"));
                }

                break;

            case "url":
                var url = ReadString(source, "url");
                if (!string.IsNullOrEmpty(url))
                {
                    parts.Add(new ImagePart(url!));
                }
                break;
        }
    }

    /// <summary>Re-serializa o elemento em JSON compacto (GetRawText preservaria a formatação da fonte).</summary>
    private static string CompactJson(JsonElement element) => Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(element));

    /// <summary>arguments de um tool_call (JSON string) → objeto JsonElement ({} se inválido).</summary>
    private static JsonElement ParseToolArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Arguments malformados no histórico: vazio em vez de quebrar a chamada.
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    /// <summary>tools OpenAI → tools Anthropic (function → name/description/input_schema).</summary>
    private static List<AnthropicTool>? MapOpenAiTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var mapped = new List<AnthropicTool>();
        foreach (var tool in tools.EnumerateArray())
        {
            var type = ReadString(tool, "type") ?? "function";
            if (type != "function")
            {
                continue;
            }

            var function = tool.TryGetProperty("function", out var f) ? f : default;
            var name = ReadString(function, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var schema = function.ValueKind == JsonValueKind.Object &&
                          function.TryGetProperty("parameters", out var p) &&
                          p.ValueKind == JsonValueKind.Object
                ? p.Clone()
                : JsonSerializer.SerializeToElement(new { type = "object" });

            mapped.Add(new AnthropicTool(name!, ReadString(function, "description"), schema));
        }

        return mapped.Count > 0 ? mapped : null;
    }

    /// <summary>tools Anthropic → tools OpenAI (input_schema → function.parameters).</summary>
    private static List<AnthropicTool>? MapAnthropicTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var mapped = new List<AnthropicTool>();
        foreach (var tool in tools.EnumerateArray())
        {
            var name = ReadString(tool, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var schema = tool.ValueKind == JsonValueKind.Object &&
                          tool.TryGetProperty("input_schema", out var s) &&
                          s.ValueKind == JsonValueKind.Object
                ? s.Clone()
                : JsonSerializer.SerializeToElement(new { type = "object" });

            mapped.Add(new AnthropicTool(name!, ReadString(tool, "description"), schema));
        }

        return mapped.Count > 0 ? mapped : null;
    }

    /// <summary>tool_choice OpenAI → objeto Anthropic (auto/required/none/nome de tool).</summary>
    private static JsonElement? MapOpenAiToolChoice(JsonElement root)
    {
        if (!root.TryGetProperty("tool_choice", out var choice))
        {
            return null;
        }

        if (choice.ValueKind == JsonValueKind.String)
        {
            var value = choice.GetString();
            return value switch
            {
                "auto" => JsonSerializer.SerializeToElement(new { type = "auto" }),
                "required" => JsonSerializer.SerializeToElement(new { type = "any" }),
                "none" => JsonSerializer.SerializeToElement(new { type = "none" }),
                // Nome de função (formato legado do OpenAI): vira tool nomeada.
                _ when !string.IsNullOrEmpty(value) => JsonSerializer.SerializeToElement(new { type = "tool", name = value }),
                _ => null,
            };
        }

        if (choice.ValueKind == JsonValueKind.Object &&
            ReadString(choice, "type") == "function" &&
            choice.TryGetProperty("function", out var f) &&
            ReadString(f, "name") is { } name)
        {
            return JsonSerializer.SerializeToElement(new { type = "tool", name });
        }

        return null;
    }

    /// <summary>tool_choice Anthropic → valor OpenAI (auto/any/none/nome de tool).</summary>
    private static JsonElement? MapAnthropicToolChoice(JsonElement root)
    {
        if (!root.TryGetProperty("tool_choice", out var choice) || choice.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(choice, "type") switch
        {
            "auto" => JsonSerializer.SerializeToElement("auto"),
            "any" => JsonSerializer.SerializeToElement("required"),
            "none" => JsonSerializer.SerializeToElement("none"),
            "tool" when ReadString(choice, "name") is { } name =>
                JsonSerializer.SerializeToElement(new { type = "function", function = new { name } }),
            _ => null,
        };
    }

    /// <summary>stop (string ou array) do OpenAI → stop_sequences da Anthropic (máx. 4).</summary>
    private static List<string>? MapStopSequences(JsonElement root)
    {
        if (!root.TryGetProperty("stop", out var stop))
        {
            return null;
        }

        var sequences = new List<string>();
        if (stop.ValueKind == JsonValueKind.String)
        {
            sequences.Add(stop.GetString()!);
        }
        else if (stop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()))
                {
                    sequences.Add(item.GetString()!);
                }
            }
        }

        // A Anthropic rejeita mais de 4 sequências: corta em vez de falhar a chamada.
        return sequences.Count > 0 ? sequences.Take(MaxStopSequences).ToList() : null;
    }

    /// <summary>Escreve usage OpenAI a partir de input/output tokens já lidos (com cache, se houver).</summary>
    private static void WriteOpenAiUsage(Utf8JsonWriter writer, JsonElement usage, long inputTokens, long outputTokens)
    {
        var cached = 0L;
        if (usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty("cache_read_input_tokens", out var cacheRead) &&
            cacheRead.ValueKind == JsonValueKind.Number)
        {
            cached = cacheRead.GetInt64();
        }

        writer.WriteStartObject("usage");
        writer.WriteNumber("prompt_tokens", inputTokens);
        writer.WriteNumber("completion_tokens", outputTokens);
        writer.WriteNumber("total_tokens", inputTokens + outputTokens);
        if (cached > 0)
        {
            writer.WriteStartObject("prompt_tokens_details");
            writer.WriteNumber("cached_tokens", cached);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>Message Anthropic: content string quando só há texto, array de blocos senão.</summary>
    private static void WriteAnthropicMessage(Utf8JsonWriter writer, OutputMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role);

        var hasNonText = message.Parts.Any(p => p is not TextPart);
        if (message.Parts.Count == 1 && !hasNonText)
        {
            writer.WriteString("content", ((TextPart)message.Parts[0]).Text);
        }
        else
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var part in message.Parts)
            {
                WriteAnthropicBlock(writer, part);
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>Bloco de conteúdo Anthropic a partir da parte intermediária.</summary>
    private static void WriteAnthropicBlock(Utf8JsonWriter writer, ContentPart part)
    {
        switch (part)
        {
            case TextPart text:
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                writer.WriteEndObject();
                break;

            case ImagePart image:
                WriteAnthropicImage(writer, image.Url);
                break;

            case ToolUsePart toolUse:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", toolUse.Id);
                writer.WriteString("name", toolUse.Name);
                writer.WritePropertyName("input");
                WriteToolInput(writer, toolUse.Input);
                writer.WriteEndObject();
                break;

            case ToolResultPart toolResult:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", toolResult.ToolCallId);
                writer.WritePropertyName("content");
                WriteToolResultContent(writer, toolResult.Content);
                writer.WriteEndObject();
                break;
        }
    }

    /// <summary>ImagePart → bloco image Anthropic (data URI vira source base64).</summary>
    private static void WriteAnthropicImage(Utf8JsonWriter writer, string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma > 0)
            {
                var meta = url["data:".Length..comma];
                var data = url[(comma + 1)..];
                var isBase64 = meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
                var mediaType = isBase64 ? meta[..^";base64".Length] : meta;

                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WriteStartObject("source");
                if (isBase64)
                {
                    writer.WriteString("type", "base64");
                    writer.WriteString("media_type", string.IsNullOrEmpty(mediaType) ? "image/png" : mediaType);
                    writer.WriteString("data", data);
                }
                else
                {
                    // data URI sem base64: exótico; encaminha como url.
                    writer.WriteString("type", "url");
                    writer.WriteString("url", url);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                return;
            }
        }

        writer.WriteStartObject();
        writer.WriteString("type", "image");
        writer.WriteStartObject("source");
        writer.WriteString("type", "url");
        writer.WriteString("url", url);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>input de tool_use: objeto quando parseável, {} senão.</summary>
    private static void WriteToolInput(Utf8JsonWriter writer, JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object || input.ValueKind == JsonValueKind.Array)
        {
            input.WriteTo(writer);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    /// <summary>content de tool_result: string ou array de blocos (vazio vira "(empty)").</summary>
    private static void WriteToolResultContent(Utf8JsonWriter writer, JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(string.IsNullOrEmpty(content.GetString()) ? "(empty)" : content.GetString());
        }
        else if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
        {
            writer.WriteStartArray();
            foreach (var block in content.EnumerateArray())
            {
                switch (ReadString(block, "type"))
                {
                    case "text":
                        var text = ReadString(block, "text");
                        if (!string.IsNullOrEmpty(text))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", text!);
                            writer.WriteEndObject();
                        }
                        break;

                    case "image":
                        var source = block.TryGetProperty("source", out var s) ? s : default;
                        if (ReadString(source, "type") == "base64")
                        {
                            WriteAnthropicImage(writer, $"data:{ReadString(source, "media_type") ?? "image/png"};base64,{ReadString(source, "data")}");
                        }
                        else if (ReadString(source, "type") == "url")
                        {
                            WriteAnthropicImage(writer, ReadString(source, "url") ?? string.Empty);
                        }

                        break;
                }
            }
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteStringValue("(empty)");
        }
    }

    /// <summary>Message OpenAI: content string quando não há imagens (tool_calls à parte), array quando há; "tool" usa tool_call_id.</summary>
    private static void WriteOpenAiMessage(Utf8JsonWriter writer, OutputMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role);

        if (message.Role == "tool")
        {
            // Por construção cada message tool carrega exatamente um ToolResultPart.
            var toolResult = (ToolResultPart)message.Parts[0];
            writer.WriteString("tool_call_id", toolResult.ToolCallId);
            writer.WritePropertyName("content");
            WriteOpenAiToolContent(writer, toolResult.Content);
            writer.WriteEndObject();
            return;
        }

        if (message.Role == "system")
        {
            writer.WriteString("content", string.Join("\n\n", message.Parts.Select(p => ((TextPart)p!).Text)));
            writer.WriteEndObject();
            return;
        }

        var texts = message.Parts.OfType<TextPart>().ToList();
        var hasImages = message.Parts.Any(p => p is ImagePart);

        if (!hasImages && texts.Count > 0)
        {
            // String mesmo com tool_calls: formato clássico aceito por todos os upstreams compatíveis.
            writer.WriteString("content", string.Join("\n\n", texts.Select(t => t.Text)));
        }
        else if (hasImages)
        {
            // Imagens exigem o formato array de partes; tool_use segue em tool_calls.
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var part in message.Parts)
            {
                WriteOpenAiContentPart(writer, part);
            }
            writer.WriteEndArray();
        }
        else
        {
            // Assistant com apenas tool_use: content nulo.
            writer.WriteNull("content");
        }

        // tool_use do assistente vira tool_calls OpenAI.
        var toolUses = message.Parts.OfType<ToolUsePart>().ToList();
        if (toolUses.Count > 0)
        {
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var toolUse in toolUses)
            {
                var arguments = toolUse.Input.ValueKind == JsonValueKind.Object || toolUse.Input.ValueKind == JsonValueKind.Array
                    ? CompactJson(toolUse.Input)
                    : "{}";
                writer.WriteStartObject();
                writer.WriteString("id", toolUse.Id);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", toolUse.Name);
                writer.WriteString("arguments", arguments);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>Parte de conteúdo → parte OpenAI (text/image_url).</summary>
    private static void WriteOpenAiContentPart(Utf8JsonWriter writer, ContentPart part)
    {
        switch (part)
        {
            case TextPart text:
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                writer.WriteEndObject();
                break;

            case ImagePart image:
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", image.Url);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;

            // tool_use/tool_result não aparecem aqui (tratados fora do content).
        }
    }

    /// <summary>content de message "tool" OpenAI: string, array ou "(empty)".</summary>
    private static void WriteOpenAiToolContent(Utf8JsonWriter writer, JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(string.IsNullOrEmpty(content.GetString()) ? "(empty)" : content.GetString());
        }
        else if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
        {
            writer.WriteStartArray();
            foreach (var block in content.EnumerateArray())
            {
                switch (ReadString(block, "type"))
                {
                    case "text":
                        var text = ReadString(block, "text");
                        if (!string.IsNullOrEmpty(text))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", text!);
                            writer.WriteEndObject();
                        }
                        break;

                    case "image":
                        var source = block.TryGetProperty("source", out var s) ? s : default;
                        if (ReadString(source, "type") == "base64")
                        {
                            WriteOpenAiContentPart(writer, new ImagePart($"data:{ReadString(source, "media_type") ?? "image/png"};base64,{ReadString(source, "data")}"));
                        }
                        else if (ReadString(source, "type") == "url")
                        {
                            WriteOpenAiContentPart(writer, new ImagePart(ReadString(source, "url") ?? string.Empty));
                        }

                        break;
                }
            }
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteStringValue("(empty)");
        }
    }

    /// <summary>Copia um número do pedido de origem, se presente.</summary>
    private static void CopyNumber(JsonElement source, Utf8JsonWriter writer, string name)
    {
        if (source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }

    /// <summary>Lê uma propriedade string não vazia (null-safe para elementos ausentes).</summary>
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

    /// <summary>
    /// Lê um inteiro positivo de uma propriedade (aceita nomes aninhados "a.b");
    /// null-safe para elementos ausentes.
    /// </summary>
    private static int? ReadPositiveInt(JsonElement element, string dottedName)
    {
        var parts = dottedName.Split('.');
        var current = element;
        foreach (var part in parts)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var size) && size > 0 ? size : null;
    }

    /// <summary>
    /// Tool results acumulados viram uma message user com blocos tool_result. Se a última
    /// message já é user (histórico malformado), funde para preservar a alternância de roles.
    /// </summary>
    private static void FlushToolResults(List<(string Id, JsonElement Content)> pending, List<OutputMessage> output)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var parts = pending.Select(p => (ContentPart)new ToolResultPart(p.Id, p.Content)).ToList();
        if (output.Count > 0 && output[^1].Role == "user")
        {
            output[^1].Parts.AddRange(parts);
        }
        else
        {
            output.Add(new OutputMessage { Role = "user", Parts = parts });
        }

        pending.Clear();
    }
}
