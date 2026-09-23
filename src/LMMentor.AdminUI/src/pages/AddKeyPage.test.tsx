import { HttpResponse, http } from 'msw';
import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AddKeyPage } from './AddKeyPage';
import { renderWithProviders } from '../test/render';
import { server } from '../test/server';
import type { Model } from '../types';

const models: Model[] = [
    {
        id: 'model-1',
        endpointId: 'endpoint-1',
        upstreamModelId: 'upstream-fast',
        displayName: 'fast',
        contextSize: 8192,
        enabled: true,
        blockedWindows: [],
    },
];

function mockModels ()
{
    server.use( http.get( '/api/models', () => HttpResponse.json( models ) ) );
}

describe( 'AddKeyPage', () =>
{
    it( 'creates the key and shows the full value exactly once', async () =>
    {
        mockModels();
        let payload: { name?: string; allowedModelIds?: string[]; } = {};
        server.use( http.post( '/api/keys', async ( { request } ) =>
        {
            payload = await request.json() as typeof payload;
            return HttpResponse.json( {
                id: 'key-1',
                name: payload.name,
                key: 'sk-lm-full-value',
                allowedModelIds: payload.allowedModelIds ?? null,
                createdAt: '2026-09-23T10:00:00Z',
            } );
        } ) );

        const { user } = renderWithProviders( <AddKeyPage /> );

        await user.type( screen.getByLabelText( /name/i ), '  IDE key  ' );
        await user.click( screen.getByRole( 'button', { name: 'Create key' } ) );

        expect( await screen.findByText( 'sk-lm-full-value' ) ).toBeInTheDocument();
        expect( payload.name ).toBe( 'IDE key' );
        // Sem modelos selecionados a chave é irrestrita.
        expect( payload.allowedModelIds ).toBeUndefined();
    } );

    it( 'does not call the API without a name', async () =>
    {
        mockModels();
        const create = vi.fn();
        server.use( http.post( '/api/keys', () =>
        {
            create();
            return HttpResponse.json( {} );
        } ) );

        const { user } = renderWithProviders( <AddKeyPage /> );
        await user.click( screen.getByRole( 'button', { name: 'Create key' } ) );

        expect( create ).not.toHaveBeenCalled();
    } );

    it( 'shows the server error when the creation fails', async () =>
    {
        mockModels();
        server.use( http.post( '/api/keys', () => HttpResponse.json( { error: 'Name is required.' }, { status: 400 } ) ) );

        const { user } = renderWithProviders( <AddKeyPage /> );

        await user.type( screen.getByLabelText( /name/i ), 'IDE key' );
        await user.click( screen.getByRole( 'button', { name: 'Create key' } ) );

        expect( await screen.findByText( 'Name is required.' ) ).toBeInTheDocument();
    } );

    it( 'copies the created key to the clipboard', async () =>
    {
        mockModels();
        server.use( http.post( '/api/keys', () => HttpResponse.json( {
            id: 'key-1',
            name: 'IDE key',
            key: 'sk-lm-full-value',
            allowedModelIds: null,
            createdAt: '2026-09-23T10:00:00Z',
        } ) ) );

        const { user } = renderWithProviders( <AddKeyPage /> );

        await user.type( screen.getByLabelText( /name/i ), 'IDE key' );
        await user.click( screen.getByRole( 'button', { name: 'Create key' } ) );
        await screen.findByText( 'sk-lm-full-value' );

        const dialog = screen.getByRole( 'dialog' );
        await user.click( within( dialog ).getByRole( 'button', { name: 'Copy key' } ) );

        // user-event instala um clipboard de teste, então o valor copiado é verificável.
        expect( await navigator.clipboard.readText() ).toBe( 'sk-lm-full-value' );
        expect( await screen.findByText( 'Copied to clipboard.' ) ).toBeInTheDocument();
    } );
} );
