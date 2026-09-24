import { HttpResponse, http } from 'msw';
import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { KeysPage } from './KeysPage';
import { renderWithProviders } from '../test/render';
import { server } from '../test/server';
import type { ApiKey, Model } from '../types';

const model: Model = {
    id: 'model-1',
    endpointId: 'endpoint-1',
    upstreamModelId: 'upstream-fast',
    displayName: 'fast',
    contextSize: 8192,
    maxOutputTokens: null,
    enabled: true,
    blockedWindows: [],
};

const activeKey: ApiKey = {
    id: 'key-1',
    name: 'IDE key',
    key: 'sk-lm-abcd…wxyz',
    allowedModelIds: null,
    createdAt: '2026-09-01T10:00:00Z',
    revokedAt: null,
};

const scopedRevokedKey: ApiKey = {
    id: 'key-2',
    name: 'Old key',
    key: 'sk-lm-efgh…stuv',
    allowedModelIds: [ 'model-1' ],
    createdAt: '2026-08-01T10:00:00Z',
    revokedAt: '2026-08-20T10:00:00Z',
};

function mockApi ( keys: ApiKey[], models: Model[] = [ model ] )
{
    server.use(
        http.get( '/api/keys', () => HttpResponse.json( keys ) ),
        http.get( '/api/models', () => HttpResponse.json( models ) ),
    );
}

describe( 'KeysPage', () =>
{
    it( 'shows an empty state when no key exists', async () =>
    {
        mockApi( [] );

        renderWithProviders( <KeysPage /> );

        expect( await screen.findByText( 'No keys created.' ) ).toBeInTheDocument();
    } );

    it( 'lists keys with their scope and status', async () =>
    {
        mockApi( [ activeKey, scopedRevokedKey ] );

        renderWithProviders( <KeysPage /> );

        const activeRow = ( await screen.findByText( 'IDE key' ) ).closest( 'tr' )!;
        expect( within( activeRow ).getByText( 'All' ) ).toBeInTheDocument();
        expect( within( activeRow ).getByText( 'Active' ) ).toBeInTheDocument();

        const revokedRow = screen.getByText( 'Old key' ).closest( 'tr' )!;
        // A chave restrita mostra o alias do modelo, não o id interno.
        expect( within( revokedRow ).getByText( 'fast' ) ).toBeInTheDocument();
        expect( within( revokedRow ).getByText( 'Revoked' ) ).toBeInTheDocument();
    } );

    it( 'revokes only active keys and refreshes the list', async () =>
    {
        let revokedId: string | null = null;
        mockApi( [ activeKey, scopedRevokedKey ] );
        server.use( http.post( '/api/keys/:id/revoke', ( { params } ) =>
        {
            revokedId = params.id as string;
            return new HttpResponse( null, { status: 204 } );
        } ) );

        const { user } = renderWithProviders( <KeysPage /> );

        const activeRow = ( await screen.findByText( 'IDE key' ) ).closest( 'tr' )!;
        await user.click( within( activeRow ).getByRole( 'button', { name: 'Revoke' } ) );

        expect( revokedId ).toBe( 'key-1' );

        // A chave revogada não oferece a ação de revogar novamente.
        const revokedRow = screen.getByText( 'Old key' ).closest( 'tr' )!;
        expect( within( revokedRow ).queryByRole( 'button', { name: 'Revoke' } ) ).toBeNull();
    } );

    it( 'deletes a key', async () =>
    {
        let deletedId: string | null = null;
        mockApi( [ activeKey ] );
        server.use( http.delete( '/api/keys/:id', ( { params } ) =>
        {
            deletedId = params.id as string;
            return new HttpResponse( null, { status: 204 } );
        } ) );

        const { user } = renderWithProviders( <KeysPage /> );

        const row = ( await screen.findByText( 'IDE key' ) ).closest( 'tr' )!;
        await user.click( within( row ).getByRole( 'button', { name: 'Delete' } ) );

        expect( deletedId ).toBe( 'key-1' );
    } );
} );
