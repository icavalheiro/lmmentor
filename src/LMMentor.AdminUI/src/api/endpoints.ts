import { request } from './http';
import type { ApiEndpoint, AvailabilityWindow, Model } from '../types';

export function getEndpoints (): Promise<ApiEndpoint[]>
{
    return request( '/api/endpoints' );
}

export function createEndpoint ( payload: { name: string; type: string; url: string; accessToken?: string; } ): Promise<ApiEndpoint>
{
    return request( '/api/endpoints', { method: 'POST', body: JSON.stringify( payload ) } );
}

export function deleteEndpoint ( id: string ): Promise<void>
{
    return request( `/api/endpoints/${ id }`, { method: 'DELETE' } );
}

export function refreshEndpoint ( id: string ): Promise<ApiEndpoint>
{
    return request( `/api/endpoints/${ id }/refresh`, { method: 'POST' } );
}

export function getEndpointModels ( endpointId: string ): Promise<Model[]>
{
    return request( `/api/endpoints/${ endpointId }/models` );
}

export function getAllModels (): Promise<Model[]>
{
    return request( '/api/models' );
}

export function renameModel ( modelId: string, displayName: string ): Promise<Model>
{
    return request( `/api/models/${ modelId }`, { method: 'PATCH', body: JSON.stringify( { displayName } ) } );
}

export function setModelEnabled ( modelId: string, enabled: boolean ): Promise<Model>
{
    return request( `/api/models/${ modelId }/enabled`, { method: 'PATCH', body: JSON.stringify( { enabled } ) } );
}

export function setModelSchedule ( modelId: string, blockedWindows: AvailabilityWindow[] ): Promise<Model>
{
    return request( `/api/models/${ modelId }/schedule`, { method: 'PATCH', body: JSON.stringify( { blockedWindows } ) } );
}
