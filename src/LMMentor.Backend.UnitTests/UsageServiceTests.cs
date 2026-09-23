using LMMentor.Backend.Data;
using LMMentor.Backend.Data.Entities;
using LMMentor.Backend.UnitTests.TestSupport;

namespace LMMentor.Backend.UnitTests;

public class UsageServiceTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly StubHttpMessageHandler _handler = new();

    public void Dispose() => _database.Dispose();

    /// <summary>Cria o serviço e devolve também o logger, cujo descarte drena a fila de gravação.</summary>
    private (UsageService Usage, UsageLogger Logger, EndpointService Endpoints, ApiKeyService Keys) CreateServices()
    {
        var logger = new UsageLogger(_database.Db);
        var endpoints = new EndpointService(_database.Db, new ModelDiscoveryService(new StubHttpClientFactory(_handler)));
        var keys = new ApiKeyService(_database.Db);
        return (new UsageService(_database.Db, logger, endpoints, keys), logger, endpoints, keys);
    }

    [Fact]
    public async Task GetSummary_AggregatesTokensAndRequests()
    {
        var (usage, logger, _, _) = CreateServices();

        usage.Log("model-a", "key-1", 100, 50, success: true);
        usage.Log("model-a", "key-1", 10, 5, success: true);
        usage.Log("model-b", null, 7, 3, success: false);
        await logger.DisposeAsync();

        var summary = usage.GetSummary(7);

        Assert.Equal(175, summary.TotalTokens);
        Assert.Equal(3, summary.TotalRequests);
        Assert.Equal(7, summary.Daily.Count);
        Assert.Equal(175, summary.Daily[^1].Tokens);
        Assert.Equal(25, summary.AvgTokensPerDay);
    }

    [Fact]
    public async Task GetSummary_BreaksDownByModelAndKeyUsingFriendlyLabels()
    {
        _handler.RespondJson("http://upstream.test/v1/models", """{ "data": [ { "id": "gpt-4o" } ] }""");
        var (usage, logger, endpoints, keys) = CreateServices();

        var endpoint = await endpoints.CreateAsync("Upstream", "openai", "http://upstream.test/v1", "", CancellationToken.None);
        var model = endpoints.GetModelsByEndpoint(endpoint.Id).Single();
        endpoints.RenameModel(model.Id, "fast");
        var key = keys.Create("CI key", null);

        usage.Log(model.Id, key.Entity.Id, 40, 60, success: true);
        await logger.DisposeAsync();

        var summary = usage.GetSummary(7);

        var byModel = Assert.Single(summary.ByModel);
        Assert.Equal("fast", byModel.Label);
        Assert.Equal(100, byModel.Tokens);

        var byKey = Assert.Single(summary.ByKey);
        Assert.Equal("CI key", byKey.Label);
        Assert.Equal(1, byKey.Requests);
    }

    [Fact]
    public async Task GetSummary_IgnoresEntriesOlderThanTheWindow()
    {
        var (usage, logger, _, _) = CreateServices();

        usage.Log("model-a", null, 10, 10, success: true);
        await logger.DisposeAsync();

        // Grava direto na coleção para simular um registro antigo.
        var collection = _database.Db.Db.GetCollection<UsageLogEntry>("usage_log");
        collection.Insert(new UsageLogEntry
        {
            Timestamp = DateTime.UtcNow.AddDays(-30),
            ModelId = "model-a",
            PromptTokens = 1000,
            CompletionTokens = 1000,
            TotalTokens = 2000,
            Success = true,
        });

        var summary = usage.GetSummary(7);

        Assert.Equal(20, summary.TotalTokens);
        Assert.Equal(1, summary.TotalRequests);
    }
}
