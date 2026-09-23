using LMMentor.Backend.Data;
using LMMentor.Backend.UnitTests.TestSupport;

namespace LMMentor.Backend.UnitTests;

public class AdminCredentialServiceTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void EnsureBootstrapped_GeneratesCredentialsOnlyOnTheFirstRun()
    {
        var service = new AdminCredentialService(_database.Db);

        var (username, password) = service.EnsureBootstrapped();
        Assert.Equal("admin", username);
        Assert.NotNull(password);
        Assert.Equal(16, password!.Length);

        var (secondUsername, secondPassword) = service.EnsureBootstrapped();
        Assert.Equal("admin", secondUsername);
        Assert.Null(secondPassword);
    }

    [Fact]
    public void Verify_AcceptsOnlyTheBootstrappedCredentials()
    {
        var service = new AdminCredentialService(_database.Db);
        var (username, password) = service.EnsureBootstrapped();

        Assert.True(service.Verify(username, password!));
        Assert.False(service.Verify(username, "wrong"));
        Assert.False(service.Verify("root", password!));
    }
}

public class ApplicationSettingsServiceTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void OllamaCompatibility_IsDisabledByDefaultAndPersistsWhenChanged()
    {
        var settings = new ApplicationSettingsService(_database.Db);

        Assert.False(settings.Get().OllamaCompatibilityEnabled);

        settings.SetOllamaCompatibilityEnabled(true);
        Assert.True(new ApplicationSettingsService(_database.Db).OllamaCompatibilityEnabled);

        settings.SetOllamaCompatibilityEnabled(false);
        Assert.False(new ApplicationSettingsService(_database.Db).OllamaCompatibilityEnabled);
    }
}
