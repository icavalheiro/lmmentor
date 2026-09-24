using System.Net;
using LMMentor.Backend.Data;
using LMMentor.Backend.Data.Entities;
using LMMentor.Backend.UnitTests.TestSupport;

namespace LMMentor.Backend.UnitTests;

public class ModelDiscoveryServiceTests
{
    private static ApiEndpointEntity Endpoint(string type, string url = "http://upstream.test/v1", string token = "") => new()
    {
        Id = "endpoint-1",
        Name = "upstream",
        Type = type,
        Url = url,
        AccessToken = token,
    };

    private static ModelDiscoveryService CreateService(StubHttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler));

    [Fact]
    public async Task DiscoverAsync_ReadsOpenAiCompatibleModelsAndContextSize()
    {
        var handler = new StubHttpMessageHandler().RespondJson(
            "http://upstream.test/v1/models",
            """
            { "data": [
                { "id": "gpt-4o", "max_model_len": 128000 },
                { "id": "gpt-4o-mini" },
                { "id": "" },
                { "no-id": true }
            ] }
            """);

        var (online, models) = await CreateService(handler).DiscoverAsync(Endpoint("vllm"), CancellationToken.None);

        Assert.True(online);
        Assert.Equal(new[] { "gpt-4o", "gpt-4o-mini" }, models.Select(m => m.UpstreamModelId).ToArray());
        Assert.Equal(128000, models[0].ContextSize);
        Assert.Null(models[1].ContextSize);
    }

    [Fact]
    public async Task DiscoverAsync_SendsTheEndpointTokenUpstream()
    {
        var handler = new StubHttpMessageHandler().RespondJson(
            "http://upstream.test/v1/models",
            """{ "data": [ { "id": "gpt-4o", "context_length": 8192 } ] }""");

        await CreateService(handler).DiscoverAsync(Endpoint("openai", token: "upstream-token"), CancellationToken.None);

        var request = handler.Requests[0].Request;
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("upstream-token", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DiscoverAsync_UsesKnownContextSizes_OnlyForOfficialCatalogs()
    {
        var handler = new StubHttpMessageHandler().RespondJson(
            "http://upstream.test/v1/models",
            """{ "data": [ { "id": "deepseek-v4-pro" } ] }""");

        var (_, remote) = await CreateService(handler).DiscoverAsync(Endpoint("deepseek"), CancellationToken.None);
        Assert.Equal(1_000_000, remote[0].ContextSize);

        var (_, local) = await CreateService(handler).DiscoverAsync(Endpoint("vllm"), CancellationToken.None);
        Assert.Null(local[0].ContextSize);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsOfflineWhenTheProviderFails()
    {
        var handler = new StubHttpMessageHandler().Respond(
            "http://upstream.test/v1/models", HttpStatusCode.InternalServerError, "boom");

        var (online, models) = await CreateService(handler).DiscoverAsync(Endpoint("openai"), CancellationToken.None);

        Assert.False(online);
        Assert.Empty(models);
    }

    [Fact]
    public async Task DiscoverAsync_ReadsOllamaTagsAndProbesContextWithShow()
    {
        var handler = new StubHttpMessageHandler()
            .RespondJson("http://ollama.test/api/tags", """{ "models": [ { "name": "llama3.1:70b" } ] }""")
            .RespondJson("http://ollama.test/api/show", """{ "model_info": { "llama.context_length": 131072 } }""");

        var endpoint = Endpoint("ollama", url: "http://ollama.test");
        var (online, models) = await CreateService(handler).DiscoverAsync(endpoint, CancellationToken.None);

        Assert.True(online);
        var model = Assert.Single(models);
        Assert.Equal("llama3.1:70b", model.UpstreamModelId);
        Assert.Equal(131072, model.ContextSize);
    }

    [Fact]
    public async Task DiscoverAsync_FallsBackToNumCtxParameterOnOllama()
    {
        var handler = new StubHttpMessageHandler()
            .RespondJson("http://ollama.test/api/tags", """{ "models": [ { "model": "qwen3:8b" } ] }""")
            .RespondJson("http://ollama.test/api/show", """{ "parameters": "stop \"<|im_end|>\"\nnum_ctx 40960" }""");

        var (_, models) = await CreateService(handler).DiscoverAsync(Endpoint("ollama", url: "http://ollama.test"), CancellationToken.None);

        Assert.Equal(40960, Assert.Single(models).ContextSize);
    }

    [Fact]
    public async Task DiscoverAsync_ReadsLlamaServerContextFromProps()
    {
        var handler = new StubHttpMessageHandler()
            .RespondJson("http://llama.test/v1/models", """{ "data": [ { "id": "local-model" } ] }""")
            .RespondJson("http://llama.test/props", """{ "default_generation_settings": { "n_ctx": 16384 }, "model_path": "/models/local-model.gguf" }""");

        var (_, models) = await CreateService(handler).DiscoverAsync(Endpoint("llamacpp", url: "http://llama.test/v1"), CancellationToken.None);

        // Sem correspondência de nome, o contexto é aplicado por ser o único modelo do endpoint.
        Assert.Equal(16384, Assert.Single(models).ContextSize);
    }

    [Fact]
    public async Task DiscoverAsync_ReadsAnthropicModelsContextAndOutputCap()
    {
        var handler = new StubHttpMessageHandler().RespondJson(
            "http://anthropic.test/v1/models?limit=1000",
            """
            { "data": [
                { "id": "claude-sonnet-4-5", "type": "model", "max_input_tokens": 200000, "max_tokens": 64000 },
                { "id": "claude-haiku-4-5", "type": "model" }
            ] }
            """);

        var (online, models) = await CreateService(handler)
            .DiscoverAsync(Endpoint("anthropic", url: "http://anthropic.test", token: "sk-ant-test"), CancellationToken.None);

        Assert.True(online);
        Assert.Equal(new[] { "claude-sonnet-4-5", "claude-haiku-4-5" }, models.Select(m => m.UpstreamModelId).ToArray());
        Assert.Equal(200000, models[0].ContextSize);
        Assert.Equal(64000, models[0].MaxOutputTokens);
        // Modelo sem os campos: ambos nulos.
        Assert.Null(models[1].ContextSize);
        Assert.Null(models[1].MaxOutputTokens);

        // Autenticação e versionamento da API Anthropic.
        var request = handler.Requests[0].Request;
        Assert.Equal("sk-ant-test", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicAdapter.ApiVersion, request.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task DiscoverAsync_AcceptsBaseUrlWithV1Suffix()
    {
        var handler = new StubHttpMessageHandler().RespondJson(
            "http://anthropic.test/v1/models?limit=1000",
            """{ "data": [ { "id": "claude-sonnet-4-5" } ] }""");

        var (online, models) = await CreateService(handler)
            .DiscoverAsync(Endpoint("anthropic", url: "http://anthropic.test/v1"), CancellationToken.None);

        Assert.True(online);
        Assert.Single(models);
    }

    [Fact]
    public async Task DiscoverAsync_FollowsAnthropicPagination()
    {
        var handler = new StubHttpMessageHandler()
            .RespondJson("http://anthropic.test/v1/models?limit=1000",
                """{ "data": [ { "id": "m-1" }, { "id": "m-2" } ], "has_more": true, "last_id": "m-2" }""")
            .RespondJson("http://anthropic.test/v1/models?limit=1000&after_id=m-2",
                """{ "data": [ { "id": "m-3" } ], "has_more": false }""");

        var (online, models) = await CreateService(handler).DiscoverAsync(Endpoint("anthropic", url: "http://anthropic.test"), CancellationToken.None);

        Assert.True(online);
        Assert.Equal(new[] { "m-1", "m-2", "m-3" }, models.Select(m => m.UpstreamModelId).ToArray());
    }

    [Fact]
    public async Task DiscoverAsync_AnthropicAuthFailureMeansOffline()
    {
        var handler = new StubHttpMessageHandler().Respond(
            "http://anthropic.test/v1/models?limit=1000", HttpStatusCode.Unauthorized, """{ "type": "error" }""");

        var (online, models) = await CreateService(handler).DiscoverAsync(Endpoint("anthropic", url: "http://anthropic.test"), CancellationToken.None);

        Assert.False(online);
        Assert.Empty(models);
    }
}
