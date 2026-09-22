using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>DTO das configurações globais expostas ao painel de administração.</summary>
public sealed record ApplicationSettingsDto(bool OllamaCompatibilityEnabled);

/// <summary>Gerencia as configurações globais persistidas do LMMentor.</summary>
public sealed class ApplicationSettingsService
{
    private const string CollectionName = "application_settings";
    private const string SettingsId = "global";

    private readonly LMMentorDb _db;

    public ApplicationSettingsService(LMMentorDb db)
    {
        _db = db;
    }

    /// <summary>Indica se a compatibilidade Ollama está habilitada.</summary>
    public bool OllamaCompatibilityEnabled => Get().OllamaCompatibilityEnabled;

    /// <summary>Obtém as configurações persistidas, usando os valores seguros padrão quando ainda não existem.</summary>
    public ApplicationSettingsDto Get()
    {
        var settings = GetCollection().FindById(SettingsId);
        return new ApplicationSettingsDto(settings?.OllamaCompatibilityEnabled ?? false);
    }

    /// <summary>Atualiza a compatibilidade da API Ollama.</summary>
    public ApplicationSettingsDto SetOllamaCompatibilityEnabled(bool enabled)
    {
        var collection = GetCollection();
        var settings = collection.FindById(SettingsId);
        if (settings is null)
        {
            settings = new ApplicationSettings { Id = SettingsId };
            collection.Insert(settings);
        }

        settings.OllamaCompatibilityEnabled = enabled;
        collection.Update(settings);
        return new ApplicationSettingsDto(settings.OllamaCompatibilityEnabled);
    }

    private LiteDB.ILiteCollection<ApplicationSettings> GetCollection() =>
        _db.Db.GetCollection<ApplicationSettings>(CollectionName);
}