namespace LMMentor.Backend.Data.Entities;

/// <summary>Chave de API pública emitida para clientes do agregador.</summary>
public sealed class ApiKeyEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Hash SHA-256 da chave, usado na autenticação.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Representação mascarada exibida ao administrador.</summary>
    public string KeyPreview { get; set; } = string.Empty;

    /// <summary>Valor legado recuperado somente para migração na inicialização.</summary>
    public string? Key { get; set; }

    /// <summary>Ids de modelos permitidos. Nulo/vazio = todos os modelos habilitados.</summary>
    public List<string>? AllowedModelIds { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Momento da revogação. Nulo = chave ativa.</summary>
    public DateTime? RevokedAt { get; set; }
}
