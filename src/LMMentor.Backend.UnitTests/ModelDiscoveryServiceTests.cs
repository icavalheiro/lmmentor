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
}
