using System.Net;
using System.Text.Json;
using LMMentor.Backend.Data;
using LMMentor.Backend.Data.Entities;
using LMMentor.Backend.UnitTests.TestSupport;

namespace LMMentor.Backend.UnitTests;

public class RelayServiceTests : IDisposable
{
    private const string ModelsUrl = "http://upstream.test/v1/models";
    private const string ChatUrl = "http://upstream.test/v1/chat/completions";

    private readonly TempDatabase _database = new();
    private readonly StubHttpMessageHandler _handler = new();
    private readonly EndpointService _endpoints;
    private readonly ApiKeyService _keys;
    private readonly ApplicationSettingsService _settings;
    private readonly UsageLogger _usageLogger;
    private readonly UsageService _usage;
    private readonly RelayService _relay;

    public RelayServiceTests()
    {
        var httpClientFactory = new StubHttpClientFactory(_handler);
        _endpoints = new EndpointService(_database.Db, new ModelDiscoveryService(httpClientFactory));
        _keys = new ApiKeyService(_database.Db);
        _settings = new ApplicationSettingsService(_database.Db);
        _usageLogger = new UsageLogger(_database.Db);
        _usage = new UsageService(_database.Db, _usageLogger, _endpoints, _keys);
        _relay = new RelayService(httpClientFactory, _endpoints, _keys, _usage, _settings);
    }

    public void Dispose()
    {
        _usageLogger.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _database.Dispose();
    }

    /// <summary>Cria um endpoint com dois modelos descobertos e habilita o primeiro com um alias.</summary>
    private async Task<(ModelDto Exposed, ModelDto Disabled)> SeedCatalogAsync()
    {
        _handler.RespondJson(ModelsUrl, """{ "data": [ { "id": "upstream-fast", "context_length": 8192 }, { "id": "upstream-slow" } ] }""");
        var endpoint = await _endpoints.CreateAsync("Upstream", "openai", "http://upstream.test/v1", "upstream-token", CancellationToken.None);

        var models = _endpoints.GetModelsByEndpoint(endpoint.Id);
        var exposed = models.Single(m => m.UpstreamModelId == "upstream-fast");
        _endpoints.RenameModel(exposed.Id, "fast");
        _endpoints.SetModelEnabled(exposed.Id, true);

        var disabled = models.Single(m => m.UpstreamModelId == "upstream-slow");
        return (_endpoints.GetAllModelDtos().Single(m => m.Id == exposed.Id), disabled);
    }

    [Fact]
    public async Task ListModels_ReturnsOnlyEnabledModelsUnderTheirExposedName()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);

        var payload = JsonSerializer.SerializeToElement(_relay.ListModels(key.Value));
        var data = payload.GetProperty("data").EnumerateArray().ToList();

        var model = Assert.Single(data);
        Assert.Equal("fast", model.GetProperty("id").GetString());
        Assert.Equal(8192, model.GetProperty("context_length").GetInt32());
    }

    [Fact]
    public async Task ListModels_HidesModelsOutsideTheKeyScope()
    {
        var (exposed, _) = await SeedCatalogAsync();
        var scopedToAnotherModel = _keys.Create("scoped", ["some-other-model-id"]);

        var payload = JsonSerializer.SerializeToElement(_relay.ListModels(scopedToAnotherModel.Value));
        Assert.Empty(payload.GetProperty("data").EnumerateArray());

        var scopedToThisModel = _keys.Create("allowed", [exposed.Id]);
        var allowed = JsonSerializer.SerializeToElement(_relay.ListModels(scopedToThisModel.Value));
        Assert.Single(allowed.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public void ListModels_RequiresAValidKey()
    {
        var missingKey = Assert.Throws<RelayException>(() => _relay.ListModels(null));
        Assert.Equal(HttpStatusCode.Unauthorized, missingKey.StatusCode);

        var unknownKey = Assert.Throws<RelayException>(() => _relay.ListModels("sk-lm-unknown"));
        Assert.Equal(HttpStatusCode.Unauthorized, unknownKey.StatusCode);
    }

    [Fact]
    public async Task ListModels_AcceptsAnonymousCallsWhenOllamaCompatibilityIsEnabled()
    {
        await SeedCatalogAsync();
        _settings.SetOllamaCompatibilityEnabled(true);

        var payload = JsonSerializer.SerializeToElement(_relay.ListModels(null));

        Assert.Single(payload.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Resolve_AcceptsBothTheAliasAndTheUpstreamId()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);

        Assert.Equal("upstream-fast", _relay.Resolve(key.Value, "fast").Model.UpstreamModelId);
        Assert.Equal("upstream-fast", _relay.Resolve(key.Value, "upstream-fast").Model.UpstreamModelId);
    }

    [Fact]
    public async Task Resolve_RejectsDisabledOrUnknownModels()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);

        var disabled = Assert.Throws<RelayException>(() => _relay.Resolve(key.Value, "upstream-slow"));
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);

        var unknown = Assert.Throws<RelayException>(() => _relay.Resolve(key.Value, "nope"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Resolve_RejectsRevokedKeys()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);
        _keys.Revoke(key.Entity.Id);

        var error = Assert.Throws<RelayException>(() => _relay.Resolve(key.Value, "fast"));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task Resolve_RejectsModelsOutsideTheKeyScope()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("scoped", ["some-other-model-id"]);

        var error = Assert.Throws<RelayException>(() => _relay.Resolve(key.Value, "fast"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task Resolve_RejectsModelsInsideABlockedWindow()
    {
        var (exposed, _) = await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);

        // Janela de 00:00 a 00:00 cruza a meia-noite e cobre o dia inteiro, qualquer que seja a hora do teste.
        var allDays = Enum.GetValues<DayOfWeek>().ToList();
        _endpoints.SetModelBlockedWindows(exposed.Id, [new ModelAvailabilityWindow { DaysOfWeek = allDays, StartTime = "00:00", EndTime = "00:00" }]);

        var error = Assert.Throws<RelayException>(() => _relay.Resolve(key.Value, "fast"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task RelayChatAsync_RewritesTheModelIdAndForwardsTheUpstreamToken()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");
        _handler.RespondJson(ChatUrl, """{ "usage": { "prompt_tokens": 12, "completion_tokens": 8 } }""");

        await _relay.RelayChatAsync(resolved, Body("""{ "model": "fast", "messages": [] }"""), CancellationToken.None);

        var forwarded = _handler.Requests.Last();
        Assert.Equal(ChatUrl, forwarded.Request.RequestUri?.ToString());
        Assert.Equal("upstream-token", forwarded.Request.Headers.Authorization?.Parameter);

        using var sent = JsonDocument.Parse(forwarded.Body);
        Assert.Equal("upstream-fast", sent.RootElement.GetProperty("model").GetString());
        Assert.False(sent.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task RelayChatAsync_RequestsUsageOnStreamingCalls()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");
        _handler.Respond(ChatUrl, HttpStatusCode.OK, "data: [DONE]\n\n", "text/event-stream");

        var response = await _relay.RelayChatAsync(resolved, Body("""{ "model": "fast", "stream": true, "stream_options": { "include_usage": false } }"""), CancellationToken.None);
        response.Body.Dispose();

        using var sent = JsonDocument.Parse(_handler.Requests.Last().Body);
        var streamOptions = sent.RootElement.GetProperty("stream_options");
        Assert.True(streamOptions.GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task RelayChatAsync_RecordsUsageFromANonStreamingResponse()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");
        _handler.RespondJson(ChatUrl, """{ "usage": { "prompt_tokens": 12, "completion_tokens": 8 } }""");

        await _relay.RelayChatAsync(resolved, Body("""{ "model": "fast", "messages": [] }"""), CancellationToken.None);
        await _usageLogger.DisposeAsync();

        var summary = _usage.GetSummary(1);
        Assert.Equal(20, summary.TotalTokens);
        Assert.Equal(1, summary.TotalRequests);
    }

    [Fact]
    public async Task RelayChatAsync_RecordsUsageFromTheStreamedUsageChunk()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");

        // O chunk com usage chega antes do [DONE]: o relay precisa encontrá-lo na janela final.
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"
            + "data: {\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":70}}\n\n"
            + "data: [DONE]\n\n";
        _handler.Respond(ChatUrl, HttpStatusCode.OK, sse, "text/event-stream");

        var response = await _relay.RelayChatAsync(resolved, Body("""{ "model": "fast", "stream": true }"""), CancellationToken.None);

        using var relayed = new MemoryStream();
        await response.Body.CopyToAsync(relayed);
        response.Body.Dispose();
        await _usageLogger.DisposeAsync();

        Assert.Equal(sse, System.Text.Encoding.UTF8.GetString(relayed.ToArray()));
        Assert.Equal(100, _usage.GetSummary(1).TotalTokens);
    }

    [Fact]
    public async Task RelayChatAsync_PropagatesUpstreamErrorsWithoutCountingSuccess()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");
        _handler.Respond(ChatUrl, HttpStatusCode.TooManyRequests, """{ "error": "rate limited" }""");

        var response = await _relay.RelayChatAsync(resolved, Body("""{ "model": "fast" }"""), CancellationToken.None);
        await _usageLogger.DisposeAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var reader = new StreamReader(response.Body);
        Assert.Contains("rate limited", await reader.ReadToEndAsync());
        Assert.Equal(0, _usage.GetSummary(1).TotalTokens);
    }

    private static MemoryStream Body(string json) => new(System.Text.Encoding.UTF8.GetBytes(json));

    // ==================== Upstream Anthropic ====================

    private const string AnthropicModelsUrl = "http://anthropic.test/v1/models?limit=1000";
    private const string AnthropicMessagesUrl = "http://anthropic.test/v1/messages";

    /// <summary>Cria um endpoint Anthropic com dois modelos e habilita o primeiro com um alias.</summary>
    private async Task SeedAnthropicCatalogAsync()
    {
        _handler.RespondJson(AnthropicModelsUrl, """{ "data": [ { "id": "claude-sonnet-4-5", "max_input_tokens": 200000, "max_tokens": 64000 }, { "id": "claude-haiku-4-5" } ] }""");
        var endpoint = await _endpoints.CreateAsync("Anthropic", "anthropic", "http://anthropic.test", "sk-ant-test", CancellationToken.None);

        var models = _endpoints.GetModelsByEndpoint(endpoint.Id);
        var exposed = models.Single(m => m.UpstreamModelId == "claude-sonnet-4-5");
        _endpoints.RenameModel(exposed.Id, "fast");
        _endpoints.SetModelEnabled(exposed.Id, true);
    }

    [Fact]
    public async Task ListAnthropicModels_ReturnsModelInfoShape()
    {
        await SeedAnthropicCatalogAsync();
        var key = _keys.Create("CI key", null);

        var payload = JsonSerializer.SerializeToElement(_relay.ListAnthropicModels(key.Value));
        var model = Assert.Single(payload.GetProperty("data").EnumerateArray());

        Assert.Equal("fast", model.GetProperty("id").GetString());
        Assert.Equal("model", model.GetProperty("type").GetString());
        Assert.Equal(200000, model.GetProperty("max_input_tokens").GetInt32());
        Assert.Equal(64000, model.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task RelayChatAsync_ConvertsOpenAiRequestForAnthropicUpstream()
    {
        await SeedAnthropicCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");
        _handler.RespondJson(AnthropicMessagesUrl, """
            {
                "id": "msg_1", "type": "message", "role": "assistant", "model": "claude-sonnet-4-5",
                "content": [{ "type": "text", "text": "Hello!" }],
                "stop_reason": "end_turn",
                "usage": { "input_tokens": 25, "output_tokens": 15 }
            }
            """);

        var response = await _relay.RelayChatAsync(resolved, Body("""
            {
                "model": "fast",
                "messages": [
                    { "role": "system", "content": "You are terse." },
                    { "role": "user", "content": "hi" }
                ]
            }
            """), CancellationToken.None);
        await _usageLogger.DisposeAsync();

        // Pedido reescrito para a API Anthropic: model real, x-api-key, version e system extraído.
        var forwarded = _handler.Requests.Last();
        Assert.Equal(AnthropicMessagesUrl, forwarded.Request.RequestUri?.ToString());
        Assert.Equal("sk-ant-test", forwarded.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicAdapter.ApiVersion, forwarded.Request.Headers.GetValues("anthropic-version").Single());

        using var sent = JsonDocument.Parse(forwarded.Body);
        Assert.Equal("claude-sonnet-4-5", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("You are terse.", sent.RootElement.GetProperty("system").GetString());
        // max_tokens obrigatório: sem valor do cliente, usa o teto descoberto do modelo.
        Assert.Equal(64000, sent.RootElement.GetProperty("max_tokens").GetInt32());

        // Resposta convertida para o formato OpenAI.
        using var reader = new StreamReader(response.Body);
        using var completion = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("chat.completion", completion.RootElement.GetProperty("object").GetString());
        Assert.Equal("Hello!", completion.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        Assert.Equal(40, _usage.GetSummary(1).TotalTokens);
    }

    [Fact]
    public async Task RelayChatAsync_StreamsAnthropicSseAsOpenAiChunks()
    {
        await SeedAnthropicCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");

        var sse = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":25,\"output_tokens\":0}}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        _handler.Respond(AnthropicMessagesUrl, HttpStatusCode.OK, sse, "text/event-stream");

        var response = await _relay.RelayChatAsync(resolved, Body("""{ "model": "fast", "stream": true, "messages": [{ "role": "user", "content": "hi" }] }"""), CancellationToken.None);

        using var relayed = new MemoryStream();
        await response.Body.CopyToAsync(relayed);
        response.Body.Dispose();
        await _usageLogger.DisposeAsync();

        var output = System.Text.Encoding.UTF8.GetString(relayed.ToArray());
        Assert.Equal("text/event-stream", response.ContentType);
        // A saída é SSE OpenAI: chunks chat.completion.chunk terminados por [DONE].
        Assert.Contains("\"object\":\"chat.completion.chunk\"", output);
        Assert.Contains("\"content\":\"hi\"", output);
        Assert.EndsWith("data: [DONE]\n\n", output);

        Assert.Equal(32, _usage.GetSummary(1).TotalTokens);
    }

    [Fact]
    public async Task RelayChatAsync_PropagatesAnthropicErrors()
    {
        await SeedAnthropicCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");
        _handler.Respond(AnthropicMessagesUrl, HttpStatusCode.TooManyRequests, """{ "type": "error", "error": { "type": "overloaded_error", "message": "Overloaded" } }""");

        var response = await _relay.RelayChatAsync(resolved, Body("""{ "model": "fast", "messages": [] }"""), CancellationToken.None);
        await _usageLogger.DisposeAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var reader = new StreamReader(response.Body);
        Assert.Contains("Overloaded", await reader.ReadToEndAsync());
        Assert.Equal(0, _usage.GetSummary(1).TotalTokens);
    }

    // ==================== /v1/messages (cliente Anthropic) ====================

    [Fact]
    public async Task RelayMessagesAsync_PassThroughToAnthropicUpstream()
    {
        await SeedAnthropicCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");
        _handler.RespondJson(AnthropicMessagesUrl, """
            {
                "id": "msg_7", "type": "message", "role": "assistant", "model": "claude-sonnet-4-5",
                "content": [{ "type": "text", "text": "ok" }],
                "stop_reason": "end_turn",
                "usage": { "input_tokens": 25, "output_tokens": 15 }
            }
            """);

        var response = await _relay.RelayMessagesAsync(resolved, Body("""
            {
                "model": "fast",
                "max_tokens": 1024,
                "system": [{ "type": "text", "text": "terse", "cache_control": { "type": "ephemeral" } }],
                "thinking": { "type": "enabled", "budget_tokens": 2048 },
                "messages": [{ "role": "user", "content": "hi" }]
            }
            """), "prompt-caching-2024-07-31", CancellationToken.None);
        await _usageLogger.DisposeAsync();

        // Pass-through: campos sem equivalente no OpenAI (cache_control, thinking) chegam intactos.
        var forwarded = _handler.Requests.Last();
        Assert.Equal(AnthropicMessagesUrl, forwarded.Request.RequestUri?.ToString());
        Assert.Equal("sk-ant-test", forwarded.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("prompt-caching-2024-07-31", forwarded.Request.Headers.GetValues("anthropic-beta").Single());

        using var sent = JsonDocument.Parse(forwarded.Body);
        Assert.Equal("claude-sonnet-4-5", sent.RootElement.GetProperty("model").GetString()); // alias reescrito
        Assert.Contains("cache_control", forwarded.Body);
        Assert.Contains("thinking", forwarded.Body);

        // Resposta devolvida como veio (formato Anthropic, bytes idênticos) + uso registrado.
        using var reader = new StreamReader(response.Body);
        var output = await reader.ReadToEndAsync();
        Assert.Contains("\"id\": \"msg_7\"", output);
        Assert.Contains("\"type\": \"message\"", output);
        Assert.Equal(40, _usage.GetSummary(1).TotalTokens);
    }

    [Fact]
    public async Task RelayMessagesAsync_ConvertsToOpenAiUpstream()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");
        _handler.RespondJson(ChatUrl, """
            {
                "id": "chat-1", "object": "chat.completion", "model": "upstream-fast",
                "choices": [{ "index": 0, "message": { "role": "assistant", "content": "Sure!" }, "finish_reason": "stop" }],
                "usage": { "prompt_tokens": 12, "completion_tokens": 8 }
            }
            """);

        var response = await _relay.RelayMessagesAsync(resolved, Body("""
            {
                "model": "fast",
                "max_tokens": 1024,
                "system": [{ "type": "text", "text": "You are terse.", "cache_control": { "type": "ephemeral" } }],
                "messages": [{ "role": "user", "content": "hi" }],
                "tools": [{ "name": "f", "description": "Does f.", "input_schema": { "type": "object" } }]
            }
            """), null, CancellationToken.None);
        await _usageLogger.DisposeAsync();

        // Pedido convertido para OpenAI: system message na frente, tools function, cache_control fora.
        var forwarded = _handler.Requests.Last();
        Assert.Equal(ChatUrl, forwarded.Request.RequestUri?.ToString());
        using var sent = JsonDocument.Parse(forwarded.Body);
        Assert.Equal("upstream-fast", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", sent.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.DoesNotContain("cache_control", forwarded.Body);
        Assert.Equal("function", sent.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());

        // Resposta convertida para o formato Anthropic.
        using var reader = new StreamReader(response.Body);
        using var message = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("message", message.RootElement.GetProperty("type").GetString());
        Assert.Equal("Sure!", message.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", message.RootElement.GetProperty("stop_reason").GetString());

        Assert.Equal(20, _usage.GetSummary(1).TotalTokens);
    }

    [Fact]
    public async Task RelayMessagesAsync_StreamsOpenAiSseAsAnthropicEvents()
    {
        await SeedCatalogAsync();
        var key = _keys.Create("CI key", null);
        var resolved = _relay.Resolve(key.Value, "fast");

        var sse = "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"model\":\"upstream-fast\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n"
            + "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"model\":\"upstream-fast\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}\n\n"
            + "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"model\":\"upstream-fast\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"model\":\"upstream-fast\",\"choices\":[],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":7}}\n\n"
            + "data: [DONE]\n\n";
        _handler.Respond(ChatUrl, HttpStatusCode.OK, sse, "text/event-stream");

        var response = await _relay.RelayMessagesAsync(resolved, Body("""{ "model": "fast", "max_tokens": 100, "stream": true, "messages": [{ "role": "user", "content": "hi" }] }"""), null, CancellationToken.None);

        using var relayed = new MemoryStream();
        await response.Body.CopyToAsync(relayed);
        response.Body.Dispose();
        await _usageLogger.DisposeAsync();

        var output = System.Text.Encoding.UTF8.GetString(relayed.ToArray());
        Assert.Equal("text/event-stream", response.ContentType);
        // A saída são eventos Anthropic na ordem protocolar.
        Assert.Contains("event: message_start", output);
        Assert.Contains("\"type\":\"text_delta\"", output);
        Assert.Contains("event: message_delta", output);
        Assert.Contains("event: message_stop", output);

        Assert.Equal(37, _usage.GetSummary(1).TotalTokens);
    }
}
