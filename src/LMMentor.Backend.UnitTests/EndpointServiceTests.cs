using LMMentor.Backend.Data;
using LMMentor.Backend.Data.Entities;
using LMMentor.Backend.UnitTests.TestSupport;

namespace LMMentor.Backend.UnitTests;

public class EndpointServiceTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly StubHttpMessageHandler _handler = new();

    public void Dispose() => _database.Dispose();

    private EndpointService CreateService() =>
        new(_database.Db, new ModelDiscoveryService(new StubHttpClientFactory(_handler)));

    private Task<ApiEndpointEntity> CreateEndpointAsync(EndpointService service) =>
        service.CreateAsync("Upstream", "openai", "http://upstream.test/v1", "", CancellationToken.None);

    [Fact]
    public async Task CreateAsync_DiscoversModelsDisabledAndMarksTheEndpointOnline()
    {
        _handler.RespondJson("http://upstream.test/v1/models", """{ "data": [ { "id": "gpt-4o", "context_length": 128000 } ] }""");
        var service = CreateService();

        var endpoint = await CreateEndpointAsync(service);

        Assert.Equal("online", endpoint.Status);
        var model = Assert.Single(service.GetModelsByEndpoint(endpoint.Id));
        Assert.Equal("gpt-4o", model.UpstreamModelId);
        Assert.Equal(128000, model.ContextSize);
        Assert.False(model.Enabled);
        Assert.Equal("", model.DisplayName);
    }

    [Fact]
    public async Task CreateAsync_MarksTheEndpointOfflineWhenDiscoveryFails()
    {
        var service = CreateService();

        var endpoint = await CreateEndpointAsync(service);

        Assert.Equal("offline", endpoint.Status);
        Assert.Empty(service.GetModelsByEndpoint(endpoint.Id));
    }

    [Fact]
    public async Task RefreshAsync_PreservesCustomizationsAndSyncsTheCatalog()
    {
        _handler.RespondJson("http://upstream.test/v1/models", """{ "data": [ { "id": "gpt-4o", "context_length": 8000 }, { "id": "legacy-model" } ] }""");
        var service = CreateService();
        var endpoint = await CreateEndpointAsync(service);

        var curated = service.GetModelsByEndpoint(endpoint.Id).Single(m => m.UpstreamModelId == "gpt-4o");
        service.RenameModel(curated.Id, "fast");
        service.SetModelEnabled(curated.Id, true);

        // O provedor perde o modelo legado, ganha um novo e reporta outro contexto.
        _handler.RespondJson("http://upstream.test/v1/models", """{ "data": [ { "id": "gpt-4o", "context_length": 128000 }, { "id": "gpt-5" } ] }""");
        await service.RefreshAsync(endpoint.Id, CancellationToken.None);

        var models = service.GetModelsByEndpoint(endpoint.Id);
        Assert.Equal(new[] { "gpt-4o", "gpt-5" }, models.Select(m => m.UpstreamModelId).ToArray());

        var refreshed = models.Single(m => m.UpstreamModelId == "gpt-4o");
        Assert.Equal("fast", refreshed.DisplayName);
        Assert.True(refreshed.Enabled);
        Assert.Equal(128000, refreshed.ContextSize);
        Assert.False(models.Single(m => m.UpstreamModelId == "gpt-5").Enabled);
    }

    [Fact]
    public async Task RefreshAsync_ReturnsNullForAnUnknownEndpoint()
    {
        var service = CreateService();

        Assert.Null(await service.RefreshAsync("does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task RenameModel_RejectsANameAlreadyExposedByAnotherModel()
    {
        _handler.RespondJson("http://upstream.test/v1/models", """{ "data": [ { "id": "gpt-4o" }, { "id": "gpt-5" } ] }""");
        var service = CreateService();
        var endpoint = await CreateEndpointAsync(service);

        var target = service.GetModelsByEndpoint(endpoint.Id).Single(m => m.UpstreamModelId == "gpt-5");

        Assert.Throws<ValidationException>(() => service.RenameModel(target.Id, "gpt-4o"));
    }

    [Fact]
    public async Task RenameModel_WithAnEmptyNameFallsBackToTheUpstreamId()
    {
        _handler.RespondJson("http://upstream.test/v1/models", """{ "data": [ { "id": "gpt-4o" } ] }""");
        var service = CreateService();
        var endpoint = await CreateEndpointAsync(service);

        var model = service.GetModelsByEndpoint(endpoint.Id).Single();
        service.RenameModel(model.Id, "fast");
        service.RenameModel(model.Id, "   ");

        var entity = service.GetAllModels().Single();
        Assert.Equal("gpt-4o", EndpointService.EffectiveName(entity));
    }

    [Fact]
    public async Task Delete_RemovesTheEndpointAndItsModels()
    {
        _handler.RespondJson("http://upstream.test/v1/models", """{ "data": [ { "id": "gpt-4o" } ] }""");
        var service = CreateService();
        var endpoint = await CreateEndpointAsync(service);

        Assert.True(service.Delete(endpoint.Id));
        Assert.Null(service.GetById(endpoint.Id));
        Assert.Empty(service.GetAllModels());
        Assert.False(service.Delete(endpoint.Id));
    }

    [Fact]
    public void RenameModel_ReturnsNullForAnUnknownModel()
    {
        var service = CreateService();

        Assert.Null(service.RenameModel("does-not-exist", "any"));
        Assert.Null(service.SetModelEnabled("does-not-exist", true));
        Assert.Null(service.SetModelBlockedWindows("does-not-exist", []));
    }
}
