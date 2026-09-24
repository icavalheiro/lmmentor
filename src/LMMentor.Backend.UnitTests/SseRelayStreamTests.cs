using System.Net;
using System.Text;
using System.Text.Json;
using LMMentor.Backend.Data;
using LMMentor.Backend.UnitTests.TestSupport;

namespace LMMentor.Backend.UnitTests;

/// <summary>
/// Testes dos conversores de stream SSE: Anthropic → OpenAI chunks, OpenAI chunks → Anthropic
/// eventos e pass-through com captura de uso. Cada teste alimenta o stream com um transcript
/// realista do provedor e verifica a saída emitida ao cliente + o uso registrado.
/// </summary>
public class SseRelayStreamTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly UsageLogger _usageLogger;
    private readonly UsageService _usage;

    public SseRelayStreamTests()
    {
        var endpoints = new EndpointService(_database.Db, new ModelDiscoveryService(new StubHttpClientFactory(new StubHttpMessageHandler())));
        var keys = new ApiKeyService(_database.Db);
        _usageLogger = new UsageLogger(_database.Db);
        _usage = new UsageService(_database.Db, _usageLogger, endpoints, keys);
    }

    public void Dispose()
    {
        _usageLogger.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _database.Dispose();
    }

    private static HttpResponseMessage StubResponse() => new(HttpStatusCode.OK);

    /// <summary>Lê a saída do stream até o fim e devolve o texto completo.</summary>
    private static string ReadAll(Stream relayed)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = relayed.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Linhas "data:" da saída SSE, na ordem.</summary>
    private static List<string> SseDataLines(string output)
    {
        var lines = new List<string>();
        foreach (var segment in output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var line in segment.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    lines.Add(line["data:".Length..].TrimStart());
                }
            }
        }

        return lines;
    }

    private static JsonElement ParseData(string data) => JsonSerializer.Deserialize<JsonElement>(data);

    // ==================== Anthropic SSE → OpenAI chunks ====================

    private const string AnthropicTranscript = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-5","stop_reason":null,"usage":{"input_tokens":25,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":":\"SF\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":40}}

        event: message_stop
        data: {"type":"message_stop"}
        """;

    [Fact]
    public async Task AnthropicToOpenAi_ConvertsFullTranscript()
    {
        var inner = new MemoryStream(Encoding.UTF8.GetBytes(AnthropicTranscript));
        using var relayed = new AnthropicToOpenAiSseStream(inner, "model-1", null, _usage, StubResponse(), CancellationToken.None, "claude-sonnet-4-5");
        var output = ReadAll(relayed);
        relayed.Dispose(); // Complete() registra o uso; depois drena a fila do logger
        await _usageLogger.DisposeAsync();

        var dataLines = SseDataLines(output);
        Assert.Equal(9, dataLines.Count); // 7 chunks + usage + [DONE]

        // Chunk inicial: role assistant, id/model do message_start.
        var first = ParseData(dataLines[0]);
        Assert.Equal("chat.completion.chunk", first.GetProperty("object").GetString());
        Assert.Equal("chatcmpl-msg_1", first.GetProperty("id").GetString());
        Assert.Equal("claude-sonnet-4-5", first.GetProperty("model").GetString());
        Assert.Equal("assistant", first.GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());

        // Deltas de texto.
        Assert.Equal("Hello", ParseData(dataLines[1]).GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(" world", ParseData(dataLines[2]).GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        // Abertura da tool call (id + name) e fragments de arguments.
        var toolStart = ParseData(dataLines[3]).GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal(0, toolStart.GetProperty("index").GetInt32());
        Assert.Equal("toolu_1", toolStart.GetProperty("id").GetString());
        Assert.Equal("get_weather", toolStart.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("", toolStart.GetProperty("function").GetProperty("arguments").GetString());

        var fragment = ParseData(dataLines[4]).GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal("{\"city\"", fragment.GetProperty("function").GetProperty("arguments").GetString());

        var fragment2 = ParseData(dataLines[5]).GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal(":\"SF\"}", fragment2.GetProperty("function").GetProperty("arguments").GetString());

        // Final: finish_reason mapeado (tool_use → tool_calls).
        var final = ParseData(dataLines[6]).GetProperty("choices")[0];
        Assert.Equal("tool_calls", final.GetProperty("finish_reason").GetString());

        // Usage chunk + [DONE].
        var usageChunk = ParseData(dataLines[7]);
        Assert.Equal(0, usageChunk.GetProperty("choices").GetArrayLength());
        Assert.Equal(25, usageChunk.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(40, usageChunk.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.Equal("[DONE]", dataLines[8]);

        // Uso registrado a partir de input (message_start) + output (message_delta).
        Assert.Equal(65, _usage.GetSummary(1).TotalTokens);
    }

    [Fact]
    public void AnthropicToOpenAi_EmitsUsageAndDoneWhenStreamIsCut()
    {
        // Sem message_stop: o EOF deve fechar com usage + [DONE].
        var cut = AnthropicTranscript.Replace("event: message_stop\ndata: {\"type\":\"message_stop\"}", "").TrimEnd();
        var inner = new MemoryStream(Encoding.UTF8.GetBytes(cut));
        using var relayed = new AnthropicToOpenAiSseStream(inner, "model-1", null, _usage, StubResponse(), CancellationToken.None, "claude-sonnet-4-5");
        var output = ReadAll(relayed);

        Assert.EndsWith("data: [DONE]\n\n", output);
    }

    [Fact]
    public void AnthropicToOpenAi_ForwardsUpstreamErrorsAsOpenAiEnvelope()
    {
        const string transcript = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_9","model":"m","usage":{"input_tokens":5}}}

            event: error
            data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}
            """;

        var inner = new MemoryStream(Encoding.UTF8.GetBytes(transcript));
        using var relayed = new AnthropicToOpenAiSseStream(inner, "model-1", null, _usage, StubResponse(), CancellationToken.None, "m");
        var output = ReadAll(relayed);

        Assert.Contains("\"error\"", output);
        Assert.Contains("Overloaded", output);
    }

    // ==================== OpenAI SSE → Anthropic events ====================

    private const string OpenAiTranscript = """
        data: {"id":"chat-9","object":"chat.completion.chunk","model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

        data: {"id":"chat-9","object":"chat.completion.chunk","model":"gpt-4o","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}

        data: {"id":"chat-9","object":"chat.completion.chunk","model":"gpt-4o","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":null}]}

        data: {"id":"chat-9","object":"chat.completion.chunk","model":"gpt-4o","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_7","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}

        data: {"id":"chat-9","object":"chat.completion.chunk","model":"gpt-4o","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":\"SF\"}"}}]},"finish_reason":null}]}

        data: {"id":"chat-9","object":"chat.completion.chunk","model":"gpt-4o","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: {"id":"chat-9","object":"chat.completion.chunk","model":"gpt-4o","choices":[],"usage":{"prompt_tokens":30,"completion_tokens":12}}

        data: [DONE]
        """;

    [Fact]
    public async Task OpenAiToAnthropic_ConvertsFullTranscript()
    {
        var inner = new MemoryStream(Encoding.UTF8.GetBytes(OpenAiTranscript));
        using var relayed = new OpenAiToAnthropicSseStream(inner, "model-1", null, _usage, StubResponse(), CancellationToken.None, "gpt-4o");
        var output = ReadAll(relayed);
        relayed.Dispose(); // Complete() registra o uso; depois drena a fila do logger
        await _usageLogger.DisposeAsync();

        // Eventos Anthropic presentes e na ordem esperada.
        Assert.Contains("event: message_start", output);
        Assert.Contains("\"type\":\"text\"", output);
        Assert.Contains("\"type\":\"input_json_delta\"", output);
        Assert.Contains("event: message_delta", output);
        Assert.Contains("event: message_stop", output);

        Assert.True(output.IndexOf("message_start", StringComparison.Ordinal) < output.IndexOf("text_delta", StringComparison.Ordinal));
        Assert.True(output.IndexOf("message_delta", StringComparison.Ordinal) > output.IndexOf("input_json_delta", StringComparison.Ordinal));
        Assert.True(output.IndexOf("event: message_stop", StringComparison.Ordinal) > output.IndexOf("event: message_delta", StringComparison.Ordinal));

        // message_start carrega id/model do chunk e input_tokens 0 (limitação conhecida).
        var start = ParseData(SseDataLines(output)[0]);
        Assert.Equal("message_start", start.GetProperty("type").GetString());
        Assert.Equal("chat-9", start.GetProperty("message").GetProperty("id").GetString());
        Assert.Equal("gpt-4o", start.GetProperty("message").GetProperty("model").GetString());
        Assert.Equal(0, start.GetProperty("message").GetProperty("usage").GetProperty("input_tokens").GetInt32());

        // Deltas de texto preservados.
        Assert.Contains("\"text\":\"Hel\"", output);
        Assert.Contains("\"text\":\"lo\"", output);

        // Bloco tool_use com id/name e fragmentos de input_json_delta.
        Assert.Contains("\"id\":\"call_7\"", output);
        Assert.Contains("\"name\":\"get_weather\"", output);
        var jsonDeltaLine = SseDataLines(output).First(l => l.Contains("input_json_delta"));
        Assert.Equal("{\"city\":\"SF\"}", ParseData(jsonDeltaLine).GetProperty("delta").GetProperty("partial_json").GetString());

        // message_delta final: stop_reason mapeado (tool_calls → tool_use) + output tokens reais.
        var deltaLine = SseDataLines(output).Last(l => l.StartsWith("{\"type\":\"message_delta\""));
        var delta = ParseData(deltaLine);
        Assert.Equal("tool_use", delta.GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.Equal(12, delta.GetProperty("usage").GetProperty("output_tokens").GetInt32());

        // Uso registrado com os valores reais do chunk de usage.
        Assert.Equal(42, _usage.GetSummary(1).TotalTokens);
    }

    [Fact]
    public void OpenAiToAnthropic_FinishesWithoutUsageChunk()
    {
        // Upstream que não reporta usage: o final ainda fecha com message_delta/stop.
        const string noUsage = """
            data: {"id":"chat-1","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":null}]}

            data: {"id":"chat-1","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]
            """;

        var inner = new MemoryStream(Encoding.UTF8.GetBytes(noUsage));
        using var relayed = new OpenAiToAnthropicSseStream(inner, "model-1", null, _usage, StubResponse(), CancellationToken.None, "m");
        var output = ReadAll(relayed);

        Assert.Contains("event: message_stop", output);
        var deltaLine = SseDataLines(output).Last(l => l.StartsWith("{\"type\":\"message_delta\""));
        Assert.Equal("end_turn", ParseData(deltaLine).GetProperty("delta").GetProperty("stop_reason").GetString());
    }

    // ==================== Pass-through (Anthropic → Anthropic) ====================

    [Fact]
    public async Task PassThrough_ForwardsEventsAndCapturesUsage()
    {
        var inner = new MemoryStream(Encoding.UTF8.GetBytes(AnthropicTranscript));
        using var relayed = new AnthropicSsePassThroughStream(inner, "model-1", null, _usage, StubResponse(), CancellationToken.None);
        var output = ReadAll(relayed);
        relayed.Dispose(); // Complete() registra o uso; depois drena a fila do logger
        await _usageLogger.DisposeAsync();

        // Os eventos transitam intactos (incluindo pings, que o Claude Code exige).
        Assert.Contains("event: message_start", output);
        Assert.Contains("event: content_block_delta", output);
        Assert.Contains("event: message_stop", output);

        // Cada evento sai terminado por linha em branco (normalização \r\n → \n).
        Assert.Equal(AnthropicTranscript.TrimEnd() + "\n\n", output);

        // Uso capturado dos eventos (input no start, output no delta).
        Assert.Equal(65, _usage.GetSummary(1).TotalTokens);
    }

    [Fact]
    public async Task PassThrough_ForwardsPingsUntouched()
    {
        const string transcript = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_p","model":"m","usage":{"input_tokens":7}}}

            event: ping
            data: {"type":"ping"}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}

            event: message_stop
            data: {"type":"message_stop"}
            """;

        var inner = new MemoryStream(Encoding.UTF8.GetBytes(transcript));
        using var relayed = new AnthropicSsePassThroughStream(inner, "model-1", null, _usage, StubResponse(), CancellationToken.None);
        var output = ReadAll(relayed);
        relayed.Dispose(); // Complete() registra o uso; depois drena a fila do logger
        await _usageLogger.DisposeAsync();

        Assert.Contains("event: ping\ndata: {\"type\":\"ping\"}\n\n", output);
        Assert.Equal(10, _usage.GetSummary(1).TotalTokens);
    }
}
