namespace LMMentor.Backend.Data.Entities;

/// <summary>Registro de uso por requisição do relay, usado para alimentar o dashboard.</summary>
public sealed class UsageLogEntry
{
    public int Id { get; set; }

    /// <summary>Momento (UTC) em que a requisição foi atendida.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Id do modelo usado. Nulo se a requisição falhou antes de atingir um modelo.</summary>
    public string? ModelId { get; set; }

    /// <summary>Id da chave de API usada. Nulo para requisições não autenticadas/rejeitadas.</summary>
    public string? ApiKeyId { get; set; }

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }

    /// <summary>Total de tokens (prompt + completion).</summary>
    public long TotalTokens { get; set; }

    /// <summary>true se a requisição foi atendida com sucesso.</summary>
    public bool Success { get; set; } = true;
}
