import { request } from './http';
import type { ApiKey } from '../types';

// Chave recém-criada: inclui o valor completo, exibido uma única vez.
export interface CreatedKey
{
    id: string;
    name: string;
    key: string;
    allowedModelIds: string[] | null;
    createdAt: string;
}

export function getKeys (): Promise<ApiKey[]>
{
    return request( '/api/keys' );
}

export function createKey ( payload: { name: string; allowedModelIds?: string[]; } ): Promise<CreatedKey>
{
    return request( '/api/keys', { method: 'POST', body: JSON.stringify( payload ) } );
}

export function revokeKey ( id: string ): Promise<void>
{
    return request( `/api/keys/${ id }/revoke`, { method: 'POST' } );
}

export function deleteKey ( id: string ): Promise<void>
{
    return request( `/api/keys/${ id }`, { method: 'DELETE' } );
}
