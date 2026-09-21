import type { ApiEndpoint, ApiKey, DailyUsage, Model, UsageByEntity } from '../types';

export const mockEndpoints: ApiEndpoint[] = [
    {
        id: 'e1',
        name: 'Primary OpenAI',
        type: 'openai',
        url: 'https://api.openai.com/v1',
        accessToken: 'sk-proj-abc123…',
        status: 'online',
        lastCheckedAt: '2026-09-21T18:42:00Z',
    },
    {
        id: 'e2',
        name: 'Local Ollama',
        type: 'ollama',
        url: 'http://localhost:11434/v1',
        accessToken: '',
        status: 'online',
        lastCheckedAt: '2026-09-21T18:42:00Z',
    },
    {
        id: 'e3',
        name: 'Fast Groq',
        type: 'groq',
        url: 'https://api.groq.com/openai/v1',
        accessToken: 'gsk_x7Yz9…',
        status: 'offline',
        lastCheckedAt: '2026-09-21T18:40:00Z',
    },
];

export const mockModels: Model[] = [
    {
        id: 'm1',
        endpointId: 'e1',
        upstreamModelId: 'gpt-4o',
        displayName: 'GPT-4o',
        contextSize: 128000,
        enabled: true,
    },
    {
        id: 'm2',
        endpointId: 'e1',
        upstreamModelId: 'gpt-4o-mini',
        displayName: '',
        contextSize: 128000,
        enabled: true,
    },
    {
        id: 'm3',
        endpointId: 'e2',
        upstreamModelId: 'llama3.1:70b',
        displayName: 'Llama 3.1 70B',
        contextSize: 131072,
        enabled: true,
    },
    {
        id: 'm4',
        endpointId: 'e3',
        upstreamModelId: 'llama-3.3-70b-versatile',
        displayName: '',
        contextSize: 131072,
        enabled: false,
    },
];

export const mockApiKeys: ApiKey[] = [
    {
        id: 'k1',
        name: 'IDE Key',
        key: 'sk-lm-Kx9mPq2vRt4wYz7aBc3dEf5g',
        allowedModelIds: null,
        createdAt: '2026-09-15T10:00:00Z',
        revokedAt: null,
    },
    {
        id: 'k2',
        name: 'CI Test',
        key: 'sk-lm-Hj6nLs8uVb1xQw4eRd9fGh2k',
        allowedModelIds: [ 'm1' ],
        createdAt: '2026-09-18T14:30:00Z',
        revokedAt: '2026-09-20T09:15:00Z',
    },
];

// Uso fake dos últimos 7 dias (datas relativas a 2026-09-21).
export const mockDailyUsage: DailyUsage[] = [
    { date: '2026-09-15', tokens: 482_000, requests: 310 },
    { date: '2026-09-16', tokens: 615_000, requests: 402 },
    { date: '2026-09-17', tokens: 389_000, requests: 255 },
    { date: '2026-09-18', tokens: 731_000, requests: 488 },
    { date: '2026-09-19', tokens: 902_000, requests: 571 },
    { date: '2026-09-20', tokens: 654_000, requests: 430 },
    { date: '2026-09-21', tokens: 512_000, requests: 344 },
];

export const mockUsageByModel: UsageByEntity[] = [
    { id: 'm1', label: 'GPT-4o', tokens: 1_840_000, requests: 920 },
    { id: 'm3', label: 'Llama 3.1 70B', tokens: 1_210_000, requests: 640 },
    { id: 'm2', label: 'gpt-4o-mini', tokens: 690_000, requests: 510 },
];

export const mockUsageByKey: UsageByEntity[] = [
    { id: 'k1', label: 'IDE Key', tokens: 2_780_000, requests: 1_430 },
    { id: 'k2', label: 'CI Test', tokens: 960_000, requests: 680 },
];
