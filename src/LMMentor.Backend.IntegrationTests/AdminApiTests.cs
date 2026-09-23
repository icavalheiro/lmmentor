using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LMMentor.Backend.IntegrationTests.TestSupport;

namespace LMMentor.Backend.IntegrationTests;

/// <summary>
/// Fluxos do painel de administração sobre a aplicação real; cada teste usa a própria
/// instância (banco temporário isolado) para não depender da ordem de execução.
/// </summary>
public class AdminApiTests
{
    private const string UpstreamUrl = "http://upstream.test/v1";
    private const string UpstreamModelsUrl = "http://upstream.test/v1/models";

    private static object NewEndpointRequest() =>
        new { name = "Upstream", type = "openai", url = UpstreamUrl, accessToken = "upstream-token" };

    [Fact]
    public async Task CreateEndpoint_DiscoversModelsDisabled()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "gpt-4o", "context_length": 128000 } ] }""");
        var client = await factory.CreateAdminClientAsync();

        var created = await LMMentorAppFactory.ReadJsonAsync(await client.PostAsJsonAsync("/api/endpoints", NewEndpointRequest()));
        var endpointId = created.GetProperty("id").GetString();

        Assert.Equal("online", created.GetProperty("status").GetString());

        var models = await LMMentorAppFactory.ReadJsonAsync(await client.GetAsync($"/api/endpoints/{endpointId}/models"));
        var model = Assert.Single(models.EnumerateArray().ToList());
        Assert.Equal("gpt-4o", model.GetProperty("upstreamModelId").GetString());
        Assert.Equal(128000, model.GetProperty("contextSize").GetInt32());
        Assert.False(model.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task CreateEndpoint_RequiresNameAndUrl()
    {
        using var factory = new LMMentorAppFactory();
        var client = await factory.CreateAdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/endpoints", new { name = "", type = "openai", url = "", accessToken = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RenameModel_RejectsDuplicateNames()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "gpt-4o" }, { "id": "gpt-5" } ] }""");
        var client = await factory.CreateAdminClientAsync();
        var endpointId = await CreateEndpointAsync(client);

        var models = await LMMentorAppFactory.ReadJsonAsync(await client.GetAsync($"/api/endpoints/{endpointId}/models"));
        var target = models.EnumerateArray().First(m => m.GetProperty("upstreamModelId").GetString() == "gpt-5");

        var response = await client.PatchAsJsonAsync($"/api/models/{target.GetProperty("id").GetString()}", new { displayName = "gpt-4o" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task SetModelSchedule_ValidatesTheWindows()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "gpt-4o" } ] }""");
        var client = await factory.CreateAdminClientAsync();
        var endpointId = await CreateEndpointAsync(client);
        var modelId = await FirstModelIdAsync(client, endpointId);

        var invalid = await client.PatchAsJsonAsync(
            $"/api/models/{modelId}/schedule",
            new { blockedWindows = new[] { new { daysOfWeek = new[] { 1 }, startTime = "nope", endTime = "18:00" } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var valid = await client.PatchAsJsonAsync(
            $"/api/models/{modelId}/schedule",
            new { blockedWindows = new[] { new { daysOfWeek = new[] { 1, 2 }, startTime = "09:00", endTime = "18:00" } } });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        var model = await LMMentorAppFactory.ReadJsonAsync(valid);
        var window = Assert.Single(model.GetProperty("blockedWindows").EnumerateArray().ToList());
        Assert.Equal("09:00", window.GetProperty("startTime").GetString());
        Assert.Equal(new[] { 1, 2 }, window.GetProperty("daysOfWeek").EnumerateArray().Select(d => d.GetInt32()).ToArray());
    }

    [Fact]
    public async Task DeleteEndpoint_RemovesItsModels()
    {
        using var factory = new LMMentorAppFactory();
        factory.Upstream.RespondJson(UpstreamModelsUrl, """{ "data": [ { "id": "gpt-4o" } ] }""");
        var client = await factory.CreateAdminClientAsync();
        var endpointId = await CreateEndpointAsync(client);

        var deleted = await client.DeleteAsync($"/api/endpoints/{endpointId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var models = await LMMentorAppFactory.ReadJsonAsync(await client.GetAsync("/api/models"));
        Assert.Empty(models.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/endpoints/{endpointId}")).StatusCode);
    }

    [Fact]
    public async Task CreateKey_ShowsTheFullValueOnlyOnce()
    {
        using var factory = new LMMentorAppFactory();
        var client = await factory.CreateAdminClientAsync();

        var created = await LMMentorAppFactory.ReadJsonAsync(await client.PostAsJsonAsync("/api/keys", new { name = "CI key" }));
        var value = created.GetProperty("key").GetString()!;
        Assert.StartsWith("sk-lm-", value);

        var listed = await LMMentorAppFactory.ReadJsonAsync(await client.GetAsync("/api/keys"));
        var stored = Assert.Single(listed.EnumerateArray().ToList());
        Assert.NotEqual(value, stored.GetProperty("key").GetString());

        var revoked = await client.PostAsync($"/api/keys/{created.GetProperty("id").GetString()}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    }

    [Fact]
    public async Task Settings_PersistOllamaCompatibility()
    {
        using var factory = new LMMentorAppFactory();
        var client = await factory.CreateAdminClientAsync();

        var initial = await LMMentorAppFactory.ReadJsonAsync(await client.GetAsync("/api/settings"));
        Assert.False(initial.GetProperty("ollamaCompatibilityEnabled").GetBoolean());

        await client.PutAsJsonAsync("/api/settings", new { ollamaCompatibilityEnabled = true });

        var updated = await LMMentorAppFactory.ReadJsonAsync(await client.GetAsync("/api/settings"));
        Assert.True(updated.GetProperty("ollamaCompatibilityEnabled").GetBoolean());
    }

    private static async Task<string> CreateEndpointAsync(HttpClient client)
    {
        var created = await LMMentorAppFactory.ReadJsonAsync(await client.PostAsJsonAsync("/api/endpoints", NewEndpointRequest()));
        return created.GetProperty("id").GetString()!;
    }

    private static async Task<string> FirstModelIdAsync(HttpClient client, string endpointId)
    {
        var models = await LMMentorAppFactory.ReadJsonAsync(await client.GetAsync($"/api/endpoints/{endpointId}/models"));
        return models.EnumerateArray().First().GetProperty("id").GetString()!;
    }
}
