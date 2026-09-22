namespace LMMentor.Backend.Data.Entities;

/// <summary>Endpoint de API upstream configurado pelo admin (OpenAI-compatible, Ollama, etc.).</summary>
public sealed class ApiEndpointEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Tipo do provedor: openai, ollama, groq, vllm, lmstudio, unsloth, custom.</summary>
    public string Type { get; set; } = "custom";

    /// <summary>URL base do endpoint (ex.: https://api.openai.com/v1).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Token de acesso em texto puro. A UI exibe apenas mascarado.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>online | offline — resultado da última verificação de status.</summary>
    public string Status { get; set; } = "offline";

    public DateTime LastCheckedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
