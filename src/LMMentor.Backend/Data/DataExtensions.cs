namespace LMMentor.Backend.Data;

/// <summary>Extensões de registro da camada de dados (LiteDB) e do bootstrap de credenciais.</summary>
public static class DataExtensions
{
    /// <summary>Registra o banco LiteDB e os serviços de dados como singletons.</summary>
    public static WebApplicationBuilder AddLmMentorData(this WebApplicationBuilder builder)
    {
        var dbPath = builder.Configuration["LMMENTOR_DB_PATH"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "lmmentor.db");

        builder.Services.AddSingleton(_ => new LMMentorDb(dbPath));
        builder.Services.AddSingleton<AdminCredentialService>();
        builder.Services.AddSingleton<ApplicationSettingsService>();
        builder.Services.AddSingleton<ModelDiscoveryService>();
        builder.Services.AddSingleton<EndpointService>();
        builder.Services.AddSingleton<ApiKeyService>();
        // IAsyncDisposable: o host drena a fila de uso no shutdown.
        builder.Services.AddSingleton<UsageLogger>();
        builder.Services.AddSingleton<UsageService>();
        builder.Services.AddSingleton<RelayService>();

        // Sem refresh agendado: a descoberta de modelos (e a leitura do contexto) roda
        // apenas quando o admin cria o endpoint ou dispara o refresh no painel.
        return builder;
    }

    /// <summary>
    /// Garante que a credencial de administrador existe; no primeiro startup imprime
    /// as credenciais geradas no console.
    /// </summary>
    public static WebApplication BootstrapAdminCredentials(this WebApplication app)
    {
        var service = app.Services.GetRequiredService<AdminCredentialService>();
        var (username, generatedPassword) = service.EnsureBootstrapped();

        if (generatedPassword is null)
        {
            return app;
        }

        Console.WriteLine();
        Console.WriteLine("LMMentor: credenciais de administrador criadas no primeiro startup.");
        Console.WriteLine($"  Usuário: {username}");
        Console.WriteLine($"  Senha:   {generatedPassword}");
        Console.WriteLine("Guarde estas credenciais; elas não serão exibidas novamente.");
        Console.WriteLine();

        return app;
    }
}
