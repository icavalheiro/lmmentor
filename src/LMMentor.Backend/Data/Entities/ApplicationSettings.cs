namespace LMMentor.Backend.Data.Entities;

/// <summary>Configurações globais persistidas do LMMentor.</summary>
public sealed class ApplicationSettings
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Habilita os endpoints de descoberta Ollama e desabilita a validação de chaves no relay público.</summary>
    public bool OllamaCompatibilityEnabled { get; set; }
}