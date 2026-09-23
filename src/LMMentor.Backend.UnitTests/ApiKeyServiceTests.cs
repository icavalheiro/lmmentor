using LMMentor.Backend.Data;
using LMMentor.Backend.UnitTests.TestSupport;

namespace LMMentor.Backend.UnitTests;

public class ApiKeyServiceTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly ApiKeyService _keys;

    public ApiKeyServiceTests()
    {
        _keys = new ApiKeyService(_database.Db);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Create_ReturnsAPrefixedKeyAndPersistsItMasked()
    {
        var created = _keys.Create("  CI key  ", null);

        Assert.StartsWith("sk-lm-", created.Value);
        Assert.Equal("CI key", created.Entity.Name);

        var stored = Assert.Single(_keys.GetAll());
        Assert.NotEqual(created.Value, stored.Key);
        Assert.Contains("…", stored.Key);
        Assert.EndsWith(created.Value[^4..], stored.Key);
    }

    [Fact]
    public void Create_TreatsAnEmptyModelListAsUnrestricted()
    {
        var created = _keys.Create("unrestricted", []);

        Assert.Null(created.Entity.AllowedModelIds);
    }

    [Fact]
    public void FindByKeyValue_MatchesOnlyTheExactKey()
    {
        var created = _keys.Create("key", null);

        Assert.Equal(created.Entity.Id, _keys.FindByKeyValue(created.Value)?.Id);
        Assert.Null(_keys.FindByKeyValue(created.Value + "x"));
        Assert.Null(_keys.FindByKeyValue(""));
    }

    [Fact]
    public void Revoke_MarksTheKeyOnceAndThenFails()
    {
        var created = _keys.Create("key", null);

        Assert.True(_keys.Revoke(created.Entity.Id));
        Assert.False(_keys.Revoke(created.Entity.Id));
        Assert.NotNull(_keys.FindByKeyValue(created.Value)?.RevokedAt);
    }

    [Fact]
    public void Delete_RemovesTheKey()
    {
        var created = _keys.Create("key", null);

        Assert.True(_keys.Delete(created.Entity.Id));
        Assert.Empty(_keys.GetAll());
        Assert.False(_keys.Delete(created.Entity.Id));
    }

    [Fact]
    public void GetAll_ReturnsTimestampsInUtc()
    {
        _keys.Create("key", null);

        var stored = Assert.Single(_keys.GetAll());
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
    }
}
