namespace LMMentor.Backend.Data.Entities;

/// <summary>Chave de API pública emitida para clientes do agregador.</summary>
public sealed class ApiKeyEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Valor completo da chave (ex.: sk-lm-...). Exibida uma única vez na criação.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Ids de modelos permitidos. Nulo/vazio = todos os modelos habilitados.</summary>
    public List<string>? AllowedModelIds { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Momento da revogação. Nulo = chave ativa.</summary>
    public DateTime? RevokedAt { get; set; }
}
