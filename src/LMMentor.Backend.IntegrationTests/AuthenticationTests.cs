using System.Net;
using System.Net.Http.Json;
using LMMentor.Backend.IntegrationTests.TestSupport;

namespace LMMentor.Backend.IntegrationTests;

public class AuthenticationTests : IClassFixture<LMMentorAppFactory>
{
    private readonly LMMentorAppFactory _factory;

    public AuthenticationTests(LMMentorAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_IsPublic()
    {
        var response = await _factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await LMMentorAppFactory.ReadJsonAsync(response);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Root_RedirectsToTheAdminUi()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task AdminApi_ReturnsUnauthorizedInsteadOfRedirecting()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/endpoints");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Login_RejectsWrongCredentials()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_CreatesASessionAndLogoutEndsIt()
    {
        var client = await _factory.CreateAdminClientAsync();

        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await LMMentorAppFactory.ReadJsonAsync(me);
        Assert.Equal(LMMentorAppFactory.AdminUsername, body.GetProperty("username").GetString());

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var afterLogout = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }
}
