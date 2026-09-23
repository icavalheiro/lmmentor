using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LMMentor.Backend.IntegrationTests.TestSupport;

namespace LMMentor.Backend.IntegrationTests;

/// <summary>Fluxo ponta a ponta da API pública: catálogo curado, chave emitida e relay.</summary>
public class RelayApiTests
{
    private const string UpstreamUrl = "http://upstream.test/v1";
    private const string UpstreamModelsUrl = "http://upstream.test/v1/models";
    private const string UpstreamChatUrl = "http://upstream.test/v1/chat/completions";

    [Fact]
    public async Task ChatCompletions_RelaysTheRequestAndRecordsUsage()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream
            .RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "upstream-fast", "context_length": 8192 } ] }""")
            .RespondJson(UpstreamChatUrl, """{ "id": "chat-1", "usage": { "prompt_tokens": 12, "completion_tokens": 8 } }""");

        var admin = await factory.CreateAdminClientAsync();
        var (modelId, _) = await PublishModelAsync(admin, alias: "fast");
        var apiKey = await CreateKeyAsync(admin, allowedModelIds: null);

        var client = CreateBearerClient(factory, apiKey);

        var catalog = await LMMentorAppFactory.ReadJsonAsync(await client.GetAsync("/v1/models"));
        var listed = Assert.Single(catalog.GetProperty("data").EnumerateArray().ToList());
        Assert.Equal("fast", listed.GetProperty("id").GetString());
        Assert.Equal(8192, listed.GetProperty("context_length").GetInt32());

        var chat = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fast",
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var completion = await LMMentorAppFactory.ReadJsonAsync(chat);
        Assert.Equal("chat-1", completion.GetProperty("id").GetString());

        // O alias público não pode vazar para o upstream: o id real é reescrito antes do envio.
        var forwarded = factory.Upstream.Requests.Last();
        Assert.Equal(UpstreamChatUrl, forwarded.Request.RequestUri?.ToString());
        Assert.Contains("\"upstream-fast\"", forwarded.Body);
        Assert.Equal("upstream-token", forwarded.Request.Headers.Authorization?.Parameter);

        var summary = await LMMentorAppFactory.WaitForUsageAsync(admin, s => s.GetProperty("totalTokens").GetInt64() == 20);
        Assert.Equal(20, summary.GetProperty("totalTokens").GetInt64());
        Assert.Equal(1, summary.GetProperty("totalRequests").GetInt64());
        Assert.Equal(modelId, summary.GetProperty("byModel").EnumerateArray().First().GetProperty("id").GetString());
    }

    [Fact]
    public async Task ChatCompletions_StreamsServerSentEventsBackToTheClient()
    {
        using var factory = new LMMentorAppFactory();
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"
            + "data: {\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":70}}\n\n"
            + "data: [DONE]\n\n";

        factory.Upstream
            .RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "upstream-fast" } ] }""")
            .Respond(UpstreamChatUrl, HttpStatusCode.OK, sse, "text/event-stream");

        var admin = await factory.CreateAdminClientAsync();
        await PublishModelAsync(admin, alias: "fast");
        var client = CreateBearerClient(factory, await CreateKeyAsync(admin, allowedModelIds: null));

        var chat = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "fast", stream = true });

        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Equal("text/event-stream", chat.Content.Headers.ContentType?.MediaType);
        Assert.Equal(sse, await chat.Content.ReadAsStringAsync());

        // O upstream recebe include_usage para que os tokens cheguem no último chunk.
        Assert.Contains("\"include_usage\":true", factory.Upstream.Requests.Last().Body);

        var summary = await LMMentorAppFactory.WaitForUsageAsync(admin, s => s.GetProperty("totalTokens").GetInt64() == 100);
        Assert.Equal(100, summary.GetProperty("totalTokens").GetInt64());
    }

    [Fact]
    public async Task PublicApi_RejectsMissingOrRevokedKeys()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "upstream-fast" } ] }""");

        var admin = await factory.CreateAdminClientAsync();
        await PublishModelAsync(admin, alias: "fast");

        var anonymous = await factory.CreateClient().GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var error = await LMMentorAppFactory.ReadJsonAsync(anonymous);
        Assert.Equal("Invalid API key.", error.GetProperty("error").GetProperty("message").GetString());

        var created = await LMMentorAppFactory.ReadJsonAsync(await admin.PostAsJsonAsync("/api/keys", new { name = "temporary" }));
        await admin.PostAsync($"/api/keys/{created.GetProperty("id").GetString()}/revoke", null);

        var revoked = CreateBearerClient(factory, created.GetProperty("key").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync("/v1/models")).StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_RejectsModelsOutsideTheKeyScope()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "upstream-fast" } ] }""");

        var admin = await factory.CreateAdminClientAsync();
        await PublishModelAsync(admin, alias: "fast");
        var client = CreateBearerClient(factory, await CreateKeyAsync(admin, allowedModelIds: ["another-model-id"]));

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "fast" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_RejectsDisabledModels()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "upstream-fast" } ] }""");

        var admin = await factory.CreateAdminClientAsync();
        await CreateEndpointAsync(admin);
        var client = CreateBearerClient(factory, await CreateKeyAsync(admin, allowedModelIds: null));

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "upstream-fast" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_PropagatesUpstreamErrors()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "upstream-fast" } ] }""");

        var admin = await factory.CreateAdminClientAsync();
        await PublishModelAsync(admin, alias: "fast");
        var client = CreateBearerClient(factory, await CreateKeyAsync(admin, allowedModelIds: null));

        // Sem stub para /chat/completions o upstream responde 404, propagado tal como veio.
        var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "fast" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OllamaCompatibility_ExposesDiscoveryOnlyWhenEnabled()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "upstream-fast", "context_length": 8192 } ] }""");

        var admin = await factory.CreateAdminClientAsync();
        await PublishModelAsync(admin, alias: "fast");
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/tags")).StatusCode);

        await admin.PutAsJsonAsync("/api/settings", new { ollamaCompatibilityEnabled = true });

        var tags = await client.GetAsync("/api/tags");
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);
        var body = await LMMentorAppFactory.ReadJsonAsync(tags);
        var model = Assert.Single(body.GetProperty("models").EnumerateArray().ToList());
        Assert.Equal("fast", model.GetProperty("name").GetString());

        // Com a compatibilidade ativa o relay público aceita chamadas sem chave.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
    }

    private static HttpClient CreateBearerClient(LMMentorAppFactory factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static async Task<string> CreateEndpointAsync(HttpClient admin)
    {
        var created = await LMMentorAppFactory.ReadJsonAsync(await admin.PostAsJsonAsync(
            "/api/endpoints",
            new { name = "Upstream", type = "openai", url = UpstreamUrl, accessToken = "upstream-token" }));

        return created.GetProperty("id").GetString()!;
    }

    /// <summary>Cria o endpoint, renomeia o modelo descoberto e o habilita na API pública.</summary>
    private static async Task<(string ModelId, string EndpointId)> PublishModelAsync(HttpClient admin, string alias)
    {
        var endpointId = await CreateEndpointAsync(admin);
        var models = await LMMentorAppFactory.ReadJsonAsync(await admin.GetAsync($"/api/endpoints/{endpointId}/models"));
        var modelId = models.EnumerateArray().First().GetProperty("id").GetString()!;

        await admin.PatchAsJsonAsync($"/api/models/{modelId}", new { displayName = alias });
        await admin.PatchAsJsonAsync($"/api/models/{modelId}/enabled", new { enabled = true });
        return (modelId, endpointId);
    }

    private static async Task<string> CreateKeyAsync(HttpClient admin, List<string>? allowedModelIds)
    {
        var created = await LMMentorAppFactory.ReadJsonAsync(await admin.PostAsJsonAsync(
            "/api/keys",
            new { name = "CI key", allowedModelIds }));

        return created.GetProperty("key").GetString()!;
    }
}
