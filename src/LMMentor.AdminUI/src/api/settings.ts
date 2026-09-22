import { request } from './http';
import type { ApplicationSettings } from '../types';

export function getSettings (): Promise<ApplicationSettings>
{
    return request( '/api/settings' );
}

export function updateSettings ( payload: ApplicationSettings ): Promise<ApplicationSettings>
{
    return request( '/api/settings', { method: 'PUT', body: JSON.stringify( payload ) } );
}