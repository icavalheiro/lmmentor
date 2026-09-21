// Tipos compartilhados da UI de administração (por enquanto apenas mocks).

export type EndpointType =
    | 'openai'
    | 'ollama'
    | 'groq'
    | 'vllm'
    | 'lmstudio'
    | 'unsloth'
    | 'custom';

export interface ApiEndpoint
{
    id: string;
    name: string;
    type: EndpointType;
    url: string;
    // Token de acesso (mock). Na UI real seria exibido apenas mascarado.
    accessToken: string;
    status: 'online' | 'offline';
    lastCheckedAt: string;
}

export interface Model
{
    id: string;
    endpointId: string;
    upstreamModelId: string;
    // Nome exposto pela API pública. Vazio = usa o nome upstream.
    displayName: string;
    contextSize: number | null;
    enabled: boolean;
}

export interface ApiKey
{
    id: string;
    name: string;
    key: string;
    allowedModelIds: string[] | null;
    createdAt: string;
    revokedAt: string | null;
}

// Uso agregado por dia (para o gráfico do dashboard).
export interface DailyUsage
{
    date: string; // ISO yyyy-mm-dd
    tokens: number;
    requests: number;
}

// Uso agregado por modelo ou por chave (para os rankings "mais usados").
export interface UsageByEntity
{
    id: string;
    label: string;
    tokens: number;
    requests: number;
}
