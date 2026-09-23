import { HttpResponse, http } from 'msw';
import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { SettingsPage } from './SettingsPage';
import { renderWithProviders } from '../test/render';
import { server } from '../test/server';

describe( 'SettingsPage', () =>
{
    it( 'reflects the persisted Ollama compatibility state', async () =>
    {
        server.use( http.get( '/api/settings', () => HttpResponse.json( { ollamaCompatibilityEnabled: true } ) ) );

        renderWithProviders( <SettingsPage /> );

        expect( await screen.findByRole( 'switch' ) ).toBeChecked();
        expect( screen.getByText( /Ollama compatibility is enabled/ ) ).toBeInTheDocument();
    } );

    it( 'persists the new value when the switch is toggled', async () =>
    {
        let saved = { ollamaCompatibilityEnabled: false };
        server.use(
            http.get( '/api/settings', () => HttpResponse.json( saved ) ),
            http.put( '/api/settings', async ( { request } ) =>
            {
                saved = await request.json() as { ollamaCompatibilityEnabled: boolean; };
                return HttpResponse.json( saved );
            } ),
        );

        const { user } = renderWithProviders( <SettingsPage /> );

        await user.click( await screen.findByRole( 'switch' ) );

        expect( saved ).toEqual( { ollamaCompatibilityEnabled: true } );
        expect( await screen.findByRole( 'switch' ) ).toBeChecked();
    } );

    it( 'shows the failure reason when the update is rejected', async () =>
    {
        server.use(
            http.get( '/api/settings', () => HttpResponse.json( { ollamaCompatibilityEnabled: false } ) ),
            http.put( '/api/settings', () => HttpResponse.json( { error: 'Storage is read-only.' }, { status: 500 } ) ),
        );

        const { user } = renderWithProviders( <SettingsPage /> );

        await user.click( await screen.findByRole( 'switch' ) );

        expect( await screen.findByText( 'Could not save settings' ) ).toBeInTheDocument();
        expect( screen.getByText( 'Storage is read-only.' ) ).toBeInTheDocument();
    } );
} );
