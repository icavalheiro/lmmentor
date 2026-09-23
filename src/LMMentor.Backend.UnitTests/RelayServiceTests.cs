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
}
