import { HttpResponse, http } from 'msw';
import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ExportSchemaButton } from './ExportSchemaButton';
import { renderWithProviders } from '../test/render';
import { server } from '../test/server';

describe( 'ExportSchemaButton', () =>
{
    it( 'opens the harness picker and automatically selects the generated JSON', async () =>
    {
        server.use( http.get( '/api/models', () => HttpResponse.json( [ {
            id: 'internal', upstreamModelId: 'upstream', displayName: 'public',
            enabled: true, contextSize: 32768, maxOutputTokens: 4096,
        } ] ) ) );
        const { user } = renderWithProviders( <ExportSchemaButton /> );
        await user.click( screen.getByRole( 'button', { name: 'Exporta schema' } ) );
        await screen.findByRole( 'button', { name: 'OpenCode' } );
        for ( const name of [ 'OpenCode', 'DeepSeek Harness', 'VS Code', 'Claude', 'pi.dev' ] )
            expect( screen.getByRole( 'button', { name } ) ).toBeInTheDocument();
        await user.click( screen.getByRole( 'button', { name: 'OpenCode' } ) );
        const textarea = await screen.findByRole( 'textbox', { name: 'Schema JSON' } ) as HTMLTextAreaElement;
        await waitFor( () =>
        {
            expect( textarea ).toHaveFocus();
            expect( textarea.selectionStart ).toBe( 0 );
            expect( textarea.selectionEnd ).toBe( textarea.value.length );
        } );
        expect( JSON.parse( textarea.value ).provider.lmmentor.models.public.limit ).toEqual( { context: 32768, output: 4096 } );
        await user.click( screen.getByRole( 'button', { name: 'Copiar JSON' } ) );
        expect( await navigator.clipboard.readText() ).toBe( textarea.value );
        expect( screen.getByRole( 'status' ) ).toHaveTextContent( 'JSON copiado.' );
        await user.click( screen.getByRole( 'button', { name: 'Trocar harness' } ) );
        await user.click( screen.getByRole( 'button', { name: 'pi.dev' } ) );
        expect( JSON.parse( ( await screen.findByRole( 'textbox', { name: 'Schema JSON' } ) as HTMLTextAreaElement ).value ).providers.lmmentor.models[0].id ).toBe( 'public' );
    } );

    it( 'explains an empty catalog and disables copying', async () =>
    {
        server.use( http.get( '/api/models', () => HttpResponse.json( [] ) ) );
        const { user } = renderWithProviders( <ExportSchemaButton /> );
        await user.click( screen.getByRole( 'button', { name: 'Exporta schema' } ) );
        await user.click( await screen.findByRole( 'button', { name: 'VS Code' } ) );
        expect( await screen.findByText( /Nenhum modelo habilitado/ ) ).toBeInTheDocument();
        expect( screen.getByRole( 'button', { name: 'Copiar JSON' } ) ).toBeDisabled();
    } );

    it( 'shows API errors and allows retrying', async () =>
    {
        server.use( http.get( '/api/models', () => HttpResponse.json( { error: 'Unavailable' }, { status: 500 } ) ) );
        const { user } = renderWithProviders( <ExportSchemaButton /> );
        await user.click( screen.getByRole( 'button', { name: 'Exporta schema' } ) );
        await user.click( await screen.findByRole( 'button', { name: 'Claude' } ) );
        expect( await screen.findByText( 'Não foi possível carregar os modelos' ) ).toBeInTheDocument();
        expect( screen.getByRole( 'button', { name: 'Copiar JSON' } ) ).toBeDisabled();
        server.use( http.get( '/api/models', () => HttpResponse.json( [] ) ) );
        await user.click( screen.getByRole( 'button', { name: 'Tentar novamente' } ) );
        expect( await screen.findByText( /Nenhum modelo habilitado/ ) ).toBeInTheDocument();
    } );
} );
