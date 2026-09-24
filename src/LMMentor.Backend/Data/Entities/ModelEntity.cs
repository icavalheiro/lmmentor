namespace LMMentor.Backend.Data.Entities;

/// <summary>Modelo descoberto em um endpoint, com nome exposto customizável e flag de habilitação.</summary>
public sealed class ModelEntity
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Id do endpoint ao qual o modelo pertence.</summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>Identificador do modelo no provedor upstream (ex.: gpt-4o, llama3.1:70b).</summary>
    public string UpstreamModelId { get; set; } = string.Empty;

    /// <summary>Nome exposto pela API pública. Vazio = usa o nome upstream.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Tamanho de contexto informado pelo provedor, se disponível.</summary>
    public int? ContextSize { get; set; }

    /// <summary>
    /// Teto de tokens de saída do modelo (ex.: max_tokens da listagem Anthropic). Usado como
    /// padrão quando um cliente OpenAI não informa max_tokens em um relay para a Anthropic.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Modelo exposto na API pública. Falso por padrão: a habilitação é opt-in.</summary>
    public bool Enabled { get; set; }

    /// <summary>Janelas recorrentes (dia da semana + horário) em que o modelo fica indisponível.</summary>
    public List<ModelAvailabilityWindow> BlockedWindows { get; set; } = new();

    public DateTime CreatedAt { get; set; }
}
