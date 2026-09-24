using System.Text;
using System.Text.Json;
using LMMentor.Backend.Data;

namespace LMMentor.Backend.UnitTests;

/// <summary>Testes dos mapeamentos OpenAI ⇄ Anthropic (pedidos e respostas não-streaming).</summary>
public class AnthropicAdapterTests
{
    private static JsonElement Parse(byte[] json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string Raw(JsonElement element) => element.GetRawText();

    // ==================== OpenAI request → Anthropic request ====================

    [Fact]
    public void BuildMessagesRequest_MapsBasicChatAndDefaultsMaxTokens()
    {
        var request = Parse("""
            {
                "model": "my-alias",
                "messages": [
                    { "role": "user", "content": "hello" },
                    { "role": "assistant", "content": "hi there" }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildMessagesRequest(request, "claude-sonnet-4-5", null));

        Assert.Equal("claude-sonnet-4-5", result.GetProperty("model").GetString());
        // max_tokens é obrigatório na Anthropic: sem valor do cliente e teto desconhecido, usa o padrão.
        Assert.Equal(8192, result.GetProperty("max_tokens").GetInt32());

        var messages = result.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        // Conteúdo só de texto sai como string (formato mais simples da API).
        Assert.Equal("hello", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("hi there", messages[1].GetProperty("content").GetString());

        // Sem system, tools, stop: os campos não aparecem.
        Assert.False(result.TryGetProperty("system", out _));
        Assert.False(result.TryGetProperty("tools", out _));
        Assert.False(result.TryGetProperty("stop_sequences", out _));
    }

    [Fact]
    public void BuildMessagesRequest_PrefersClientMaxTokensThenKnownModelCap()
    {
        var withClientValue = Parse("""{ "model": "m", "max_tokens": 1234, "messages": [] }""");
        Assert.Equal(1234, Parse(AnthropicAdapter.BuildMessagesRequest(withClientValue, "m", 4096)).GetProperty("max_tokens").GetInt32());

        var withCompletionTokens = Parse("""{ "model": "m", "max_completion_tokens": 555, "messages": [] }""");
        Assert.Equal(555, Parse(AnthropicAdapter.BuildMessagesRequest(withCompletionTokens, "m", 4096)).GetProperty("max_tokens").GetInt32());

        var without = Parse("""{ "model": "m", "messages": [] }""");
        // Cliente não informa: cai para o teto descoberto do modelo.
        Assert.Equal(4096, Parse(AnthropicAdapter.BuildMessagesRequest(without, "m", 4096)).GetProperty("max_tokens").GetInt32());

        var zero = Parse("""{ "model": "m", "max_tokens": 0, "messages": [] }""");
        // Valor inválido (0) também cai para o teto conhecido.
        Assert.Equal(4096, Parse(AnthropicAdapter.BuildMessagesRequest(zero, "m", 4096)).GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void BuildMessagesRequest_ExtractsSystemIntoTopLevelParameter()
    {
        var request = Parse("""
            {
                "model": "m",
                "messages": [
                    { "role": "system", "content": "You are terse." },
                    { "role": "user", "content": "hi" }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildMessagesRequest(request, "m", null));

        Assert.Equal("You are terse.", result.GetProperty("system").GetString());
        var messages = result.GetProperty("messages");
        Assert.Single(messages.EnumerateArray());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public void BuildMessagesRequest_JoinsMultipleSystemMessages()
    {
        var request = Parse("""
            {
                "model": "m",
                "messages": [
                    { "role": "system", "content": "first" },
                    { "role": "system", "content": "second" },
                    { "role": "user", "content": "hi" }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildMessagesRequest(request, "m", null));
        Assert.Equal("first\n\nsecond", result.GetProperty("system").GetString());
    }

    [Fact]
    public void BuildMessagesRequest_MapsToolCallsAndResultsToAnthropicBlocks()
    {
        var request = Parse("""
            {
                "model": "m",
                "messages": [
                    { "role": "user", "content": "weather in SF?" },
                    {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [
                            { "id": "call_1", "type": "function", "function": { "name": "get_weather", "arguments": "{\"city\":\"SF\"}" } }
                        ]
                    },
                    { "role": "tool", "tool_call_id": "call_1", "content": "sunny, 18C" }
                ],
                "tools": [
                    { "type": "function", "function": { "name": "get_weather", "description": "Gets weather.", "parameters": { "type": "object", "properties": { "city": { "type": "string" } } } } }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildMessagesRequest(request, "m", null));
        var messages = result.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        // Assistant: tool_call vira bloco tool_use com input parseado.
        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var blocks = assistant.GetProperty("content");
        Assert.Single(blocks.EnumerateArray());
        Assert.Equal("tool_use", blocks[0].GetProperty("type").GetString());
        Assert.Equal("call_1", blocks[0].GetProperty("id").GetString());
        Assert.Equal("get_weather", blocks[0].GetProperty("name").GetString());
        Assert.Equal("SF", blocks[0].GetProperty("input").GetProperty("city").GetString());

        // Resultado da tool: message user com bloco tool_result.
        var toolResultMessage = messages[2];
        Assert.Equal("user", toolResultMessage.GetProperty("role").GetString());
        var resultBlocks = toolResultMessage.GetProperty("content");
        Assert.Single(resultBlocks.EnumerateArray());
        Assert.Equal("tool_result", resultBlocks[0].GetProperty("type").GetString());
        Assert.Equal("call_1", resultBlocks[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("sunny, 18C", resultBlocks[0].GetProperty("content").GetString());

        // Tools mapeadas para o formato Anthropic.
        var tool = result.GetProperty("tools")[0];
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.Equal("Gets weather.", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Fact]
    public void BuildMessagesRequest_MergesConsecutiveToolResultsIntoOneUserMessage()
    {
        var request = Parse("""
            {
                "model": "m",
                "messages": [
                    {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [
                            { "id": "call_1", "type": "function", "function": { "name": "a", "arguments": "{}" } },
                            { "id": "call_2", "type": "function", "function": { "name": "b", "arguments": "{}" } }
                        ]
                    },
                    { "role": "tool", "tool_call_id": "call_1", "content": "r1" },
                    { "role": "tool", "tool_call_id": "call_2", "content": "r2" }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildMessagesRequest(request, "m", null));
        var messages = result.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());

        // Os dois resultados chegam em uma única message user (a Anthropic exige alternância).
        var blocks = messages[1].GetProperty("content");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal("call_1", blocks[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("call_2", blocks[1].GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public void BuildMessagesRequest_MapsToolChoiceAndStopSequences()
    {
        var request = Parse("""
            {
                "model": "m",
                "messages": [{ "role": "user", "content": "hi" }],
                "temperature": 0.4,
                "top_p": 0.9,
                "stop": ["END"],
                "tool_choice": "required",
                "tools": [{ "type": "function", "function": { "name": "f" } }]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildMessagesRequest(request, "m", null));

        Assert.Equal("any", result.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal(0.4, result.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.9, result.GetProperty("top_p").GetDouble(), 3);
        var stops = result.GetProperty("stop_sequences");
        Assert.Single(stops.EnumerateArray());
        Assert.Equal("END", stops[0].GetString());

        // Tool sem parameters: input_schema padrão de objeto vazio.
        Assert.Equal("object", result.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Fact]
    public void BuildMessagesRequest_MapsNamedToolChoiceAndMetadata()
    {
        var request = Parse("""
            {
                "model": "m",
                "messages": [{ "role": "user", "content": "hi" }],
                "tool_choice": { "type": "function", "function": { "name": "must_use" } },
                "tools": [{ "type": "function", "function": { "name": "must_use" } }],
                "user": "dev-42"
            }
            """);

        var result = Parse(AnthropicAdapter.BuildMessagesRequest(request, "m", null));
        Assert.Equal("tool", result.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("must_use", result.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.Equal("dev-42", result.GetProperty("metadata").GetProperty("user_id").GetString());
    }

    [Fact]
    public void BuildMessagesRequest_MapsImagesDataUriAndUrl()
    {
        var request = Parse("""
            {
                "model": "m",
                "messages": [
                    {
                        "role": "user",
                        "content": [
                            { "type": "text", "text": "what is this?" },
                            { "type": "image_url", "image_url": { "url": "data:image/png;base64,QUJD" } },
                            { "type": "image_url", "image_url": { "url": "https://img.example.com/a.jpg" } }
                        ]
                    }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildMessagesRequest(request, "m", null));
        var blocks = result.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(3, blocks.GetArrayLength());

        Assert.Equal("text", blocks[0].GetProperty("type").GetString());
        Assert.Equal("base64", blocks[1].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/png", blocks[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("QUJD", blocks[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("url", blocks[2].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("https://img.example.com/a.jpg", blocks[2].GetProperty("source").GetProperty("url").GetString());
    }

    [Fact]
    public void BuildMessagesRequest_PropagatesStreamFlag()
    {
        var streaming = Parse("""{ "model": "m", "stream": true, "messages": [] }""");
        Assert.True(Parse(AnthropicAdapter.BuildMessagesRequest(streaming, "m", null)).GetProperty("stream").GetBoolean());

        var nonStreaming = Parse("""{ "model": "m", "messages": [] }""");
        Assert.False(Parse(AnthropicAdapter.BuildMessagesRequest(nonStreaming, "m", null)).TryGetProperty("stream", out _));
    }

    // ==================== Anthropic request → OpenAI request ====================

    [Fact]
    public void BuildChatCompletionRequest_MapsSystemArrayDroppingCacheControl()
    {
        var request = Parse("""
            {
                "model": "claude-sonnet-4-5",
                "max_tokens": 1024,
                "system": [
                    { "type": "text", "text": "You are terse.", "cache_control": { "type": "ephemeral" } },
                    { "type": "text", "text": "Be kind." }
                ],
                "messages": [{ "role": "user", "content": "hi" }]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildChatCompletionRequest(request, "gpt-4o"));

        Assert.Equal("gpt-4o", result.GetProperty("model").GetString());
        Assert.Equal(1024, result.GetProperty("max_tokens").GetInt32());

        var messages = result.GetProperty("messages");
        // System vira a primeira message do histórico.
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are terse.\n\nBe kind.", messages[0].GetProperty("content").GetString());
        // cache_control não tem equivalente: some da conversão.
        Assert.DoesNotContain("cache_control", Raw(result));

        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void BuildChatCompletionRequest_MapsToolUseAndResultsToOpenAiHistory()
    {
        var request = Parse("""
            {
                "model": "m",
                "max_tokens": 100,
                "messages": [
                    { "role": "user", "content": "weather?" },
                    {
                        "role": "assistant",
                        "content": [
                            { "type": "text", "text": "checking" },
                            { "type": "tool_use", "id": "toolu_1", "name": "get_weather", "input": { "city": "SF" } }
                        ]
                    },
                    {
                        "role": "user",
                        "content": [
                            { "type": "tool_result", "tool_use_id": "toolu_1", "content": "sunny" }
                        ]
                    }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildChatCompletionRequest(request, "gpt-4o"));
        var messages = result.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        // Assistant: texto + tool_use → content + tool_calls.
        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("checking", assistant.GetProperty("content").GetString());
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("toolu_1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"city\":\"SF\"}", call.GetProperty("function").GetProperty("arguments").GetString());

        // tool_result vira message role "tool".
        var toolMessage = messages[2];
        Assert.Equal("tool", toolMessage.GetProperty("role").GetString());
        Assert.Equal("toolu_1", toolMessage.GetProperty("tool_call_id").GetString());
        Assert.Equal("sunny", toolMessage.GetProperty("content").GetString());
    }

    [Fact]
    public void BuildChatCompletionRequest_MapsToolsToolChoiceStopAndStream()
    {
        var request = Parse("""
            {
                "model": "m",
                "max_tokens": 100,
                "temperature": 0.5,
                "top_p": 0.8,
                "stop_sequences": ["END"],
                "stream": true,
                "messages": [{ "role": "user", "content": "hi" }],
                "tools": [
                    { "name": "f", "description": "Does f.", "input_schema": { "type": "object", "properties": {} } }
                ],
                "tool_choice": { "type": "any" }
            }
            """);

        var result = Parse(AnthropicAdapter.BuildChatCompletionRequest(request, "gpt-4o"));

        Assert.Equal(0.5, result.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.8, result.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal("END", result.GetProperty("stop").GetString());
        Assert.True(result.GetProperty("stream").GetBoolean());

        // Stream OpenAI pede usage explícito no último chunk.
        Assert.True(result.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        var tool = result.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("f", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Does f.", tool.GetProperty("function").GetProperty("description").GetString());

        // any → required (o OpenAI não tem "any").
        Assert.Equal("required", result.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public void BuildChatCompletionRequest_MapsNamedToolChoiceAndDropsThinking()
    {
        var request = Parse("""
            {
                "model": "m",
                "max_tokens": 100,
                "thinking": { "type": "enabled", "budget_tokens": 2048 },
                "tool_choice": { "type": "tool", "name": "f" },
                "tools": [{ "name": "f", "input_schema": { "type": "object" } }],
                "messages": [
                    {
                        "role": "assistant",
                        "content": [
                            { "type": "thinking", "thinking": "hmm", "signature": "sig" },
                            { "type": "text", "text": "answer" }
                        ]
                    },
                    { "role": "user", "content": "next" }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildChatCompletionRequest(request, "gpt-4o"));

        // thinking não tem equivalente OpenAI: some por completo.
        Assert.DoesNotContain("thinking", Raw(result));

        var choice = result.GetProperty("tool_choice");
        Assert.Equal("function", choice.GetProperty("type").GetString());
        Assert.Equal("f", choice.GetProperty("function").GetProperty("name").GetString());

        // Bloco thinking descartado: o assistant fica só com o texto.
        var assistant = result.GetProperty("messages")[0];
        Assert.Equal("answer", assistant.GetProperty("content").GetString());
    }

    [Fact]
    public void BuildChatCompletionRequest_MapsImagesBothDirections()
    {
        var request = Parse("""
            {
                "model": "m",
                "max_tokens": 100,
                "messages": [
                    {
                        "role": "user",
                        "content": [
                            { "type": "text", "text": "see" },
                            { "type": "image", "source": { "type": "base64", "media_type": "image/jpeg", "data": "REME" } },
                            { "type": "image", "source": { "type": "url", "url": "https://img.example.com/b.png" } }
                        ]
                    }
                ]
            }
            """);

        var result = Parse(AnthropicAdapter.BuildChatCompletionRequest(request, "gpt-4o"));
        var parts = result.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(3, parts.GetArrayLength());

        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/jpeg;base64,REME", parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("image_url", parts[2].GetProperty("type").GetString());
        Assert.Equal("https://img.example.com/b.png", parts[2].GetProperty("image_url").GetProperty("url").GetString());
    }

    // ==================== Anthropic response → OpenAI completion ====================

    [Fact]
    public void ToOpenAiCompletion_MapsTextUsageAndFinishReason()
    {
        var response = Parse("""
            {
                "id": "msg_01",
                "type": "message",
                "role": "assistant",
                "model": "claude-sonnet-4-5",
                "content": [{ "type": "text", "text": "Hello!" }],
                "stop_reason": "end_turn",
                "usage": { "input_tokens": 25, "output_tokens": 15 }
            }
            """);

        var result = Parse(AnthropicAdapter.ToOpenAiCompletion(response, "claude-sonnet-4-5"));

        Assert.Equal("chatcmpl-msg_01", result.GetProperty("id").GetString());
        Assert.Equal("chat.completion", result.GetProperty("object").GetString());
        Assert.Equal("claude-sonnet-4-5", result.GetProperty("model").GetString());

        var choice = result.GetProperty("choices")[0];
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Hello!", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());

        var usage = result.GetProperty("usage");
        Assert.Equal(25, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(15, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(40, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void ToOpenAiCompletion_MapsToolUseToToolCallsAndFinishReason()
    {
        var response = Parse("""
            {
                "id": "msg_02",
                "type": "message",
                "role": "assistant",
                "model": "claude-sonnet-4-5",
                "content": [
                    { "type": "text", "text": "let me check" },
                    { "type": "tool_use", "id": "toolu_9", "name": "get_weather", "input": { "city": "NY" } }
                ],
                "stop_reason": "tool_use",
                "usage": { "input_tokens": 10, "output_tokens": 20 }
            }
            """);

        var result = Parse(AnthropicAdapter.ToOpenAiCompletion(response, "claude-sonnet-4-5"));
        var message = result.GetProperty("choices")[0].GetProperty("message");

        Assert.Equal("let me check", message.GetProperty("content").GetString());
        var call = message.GetProperty("tool_calls")[0];
        Assert.Equal("toolu_9", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"city\":\"NY\"}", call.GetProperty("function").GetProperty("arguments").GetString());

        Assert.Equal("tool_calls", result.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Theory]
    [InlineData("max_tokens", "length")]
    [InlineData("stop_sequence", "stop")]
    [InlineData(null, "stop")]
    public void ToOpenAiCompletion_MapsStopReasons(string? stopReason, string expectedFinish)
    {
        var content = """[{ "type": "text", "text": "x" }]""";
        var responseJson = $"{{ \"id\": \"m\", \"model\": \"mm\", \"content\": {content}, \"stop_reason\": {(stopReason is null ? "null" : $"\"{stopReason}\"")}, \"usage\": {{}} }}";
        var result = Parse(AnthropicAdapter.ToOpenAiCompletion(Parse(responseJson), "mm"));
        Assert.Equal(expectedFinish, result.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void ToOpenAiCompletion_MapsCacheReadToPromptDetails()
    {
        var response = Parse("""
            {
                "id": "msg_03",
                "model": "m",
                "content": [{ "type": "text", "text": "x" }],
                "stop_reason": "end_turn",
                "usage": { "input_tokens": 100, "output_tokens": 5, "cache_read_input_tokens": 80 }
            }
            """);

        var usage = Parse(AnthropicAdapter.ToOpenAiCompletion(response, "m")).GetProperty("usage");
        Assert.Equal(80, usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32());
    }

    // ==================== OpenAI completion → Anthropic message ====================

    [Fact]
    public void ToAnthropicMessage_MapsContentToolCallsAndUsage()
    {
        var response = Parse("""
            {
                "id": "chat-7",
                "object": "chat.completion",
                "model": "gpt-4o",
                "choices": [
                    {
                        "index": 0,
                        "message": {
                            "role": "assistant",
                            "content": "Sure!",
                            "tool_calls": [
                                { "id": "call_5", "type": "function", "function": { "name": "f", "arguments": "{\"a\":1}" } }
                            ]
                        },
                        "finish_reason": "tool_calls"
                    }
                ],
                "usage": { "prompt_tokens": 30, "completion_tokens": 12, "total_tokens": 42 }
            }
            """);

        var result = Parse(AnthropicAdapter.ToAnthropicMessage(response, "gpt-4o"));

        Assert.Equal("chat-7", result.GetProperty("id").GetString());
        Assert.Equal("message", result.GetProperty("type").GetString());
        Assert.Equal("assistant", result.GetProperty("role").GetString());
        Assert.Equal("gpt-4o", result.GetProperty("model").GetString());

        var blocks = result.GetProperty("content");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal("text", blocks[0].GetProperty("type").GetString());
        Assert.Equal("Sure!", blocks[0].GetProperty("text").GetString());
        Assert.Equal("tool_use", blocks[1].GetProperty("type").GetString());
        Assert.Equal("call_5", blocks[1].GetProperty("id").GetString());
        Assert.Equal("f", blocks[1].GetProperty("name").GetString());
        Assert.Equal(1, blocks[1].GetProperty("input").GetProperty("a").GetInt32());

        Assert.Equal("tool_use", result.GetProperty("stop_reason").GetString());

        var usage = result.GetProperty("usage");
        Assert.Equal(30, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public void ToAnthropicMessage_MapsCachedTokensAndFinishReasons()
    {
        var withCache = Parse("""
            {
                "id": "c1",
                "model": "m",
                "choices": [{ "message": { "role": "assistant", "content": "x" }, "finish_reason": "length" }],
                "usage": { "prompt_tokens": 50, "completion_tokens": 10, "prompt_tokens_details": { "cached_tokens": 40 } }
            }
            """);

        var result = Parse(AnthropicAdapter.ToAnthropicMessage(withCache, "m"));
        Assert.Equal("max_tokens", result.GetProperty("stop_reason").GetString());
        Assert.Equal(40, result.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32());

        var stop = Parse("""{ "id": "c2", "model": "m", "choices": [{ "message": { "role": "assistant", "content": "y" }, "finish_reason": "stop" }], "usage": {} }""");
        Assert.Equal("end_turn", Parse(AnthropicAdapter.ToAnthropicMessage(stop, "m")).GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void V1Root_GuaranteesV1Suffix()
    {
        Assert.Equal("https://api.anthropic.com/v1", AnthropicAdapter.V1Root("https://api.anthropic.com"));
        Assert.Equal("https://api.anthropic.com/v1", AnthropicAdapter.V1Root("https://api.anthropic.com/v1"));
        Assert.Equal("https://api.anthropic.com/v1", AnthropicAdapter.V1Root("https://api.anthropic.com/"));
        Assert.Equal("http://proxy.test:8080/base/v1", AnthropicAdapter.V1Root("http://proxy.test:8080/base"));
    }
}
