using System.Net.Http.Json;
using System.Text.Json;
using LiteDB;
using LMMentor.Backend.Data;
using LMMentor.Backend.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LMMentor.Backend.IntegrationTests.TestSupport;

/// <summary>
/// Sobe a aplicação real em memória com um banco LiteDB temporário (credencial de admin
/// pré-semeada) e um upstream HTTP falso no lugar do IHttpClientFactory.
/// </summary>
public sealed class LMMentorAppFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "integration-password";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lmmentor-it-{Guid.NewGuid():N}.db");

    public LMMentorAppFactory()
    {
        SeedAdminCredential();
    }

    /// <summary>Upstream falso usado pela descoberta de modelos e pelo relay.</summary>
    public StubHttpMessageHandler Upstream { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("LMMENTOR_DB_PATH", _dbPath);
        builder.ConfigureServices(services =>
        {
            // Registro posterior vence na resolução: nenhuma chamada sai para a rede.
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(Upstream));
        });
    }

    /// <summary>Cliente já autenticado com o cookie de sessão do admin.</summary>
    public async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = AdminUsername, password = AdminPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>
    /// Lê a resposta como JSON. Os endpoints usam camelCase, então as propriedades são
    /// acessadas com o mesmo nome exposto ao frontend.
    /// </summary>
    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// O uso é gravado por um consumidor assíncrono: tenta novamente por alguns instantes
    /// até o registro aparecer no resumo.
    /// </summary>
    public static async Task<JsonElement> WaitForUsageAsync(HttpClient client, Func<JsonElement, bool> predicate)
    {
        JsonElement summary = default;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            summary = await ReadJsonAsync(await client.GetAsync("/api/usage/summary?days=1"));
            if (predicate(summary))
            {
                return summary;
            }

            await Task.Delay(100);
        }

        return summary;
    }

    private void SeedAdminCredential()
    {
        var (hash, salt) = PasswordHasher.Hash(AdminPassword);
        using var db = new LiteDatabase(_dbPath);
        db.GetCollection<AdminCredential>("admin_credentials").Insert(new AdminCredential
        {
            Username = AdminUsername,
            PasswordHash = hash,
            Salt = salt,
            CreatedAt = DateTime.UtcNow,
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (var file in new[] { _dbPath, _dbPath + "-log" })
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Arquivo ainda travado pelo SO: o diretório temporário é limpo depois.
        }
    }
}
