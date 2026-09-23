import { HttpResponse, http } from 'msw';
import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { LoginPage } from './LoginPage';
import { renderWithProviders } from '../test/render';
import { server } from '../test/server';

/**
 * Substitui window.location para observar o redirecionamento sem navegar de verdade no jsdom.
 * O href inicial é o real (absoluto), senão o fetch não resolve os caminhos relativos da API.
 */
function stubLocation ()
{
    const stub = { href: window.location.href };
    Object.defineProperty( window, 'location', { configurable: true, value: stub } );
    return stub;
}

describe( 'LoginPage', () =>
{
    it( 'submits the typed credentials and redirects to the admin UI', async () =>
    {
        const location = stubLocation();
        const received: { username?: string; password?: string; } = {};
        server.use( http.post( '/api/auth/login', async ( { request } ) =>
        {
            Object.assign( received, await request.json() as object );
            return HttpResponse.json( { ok: true } );
        } ) );

        const { user } = renderWithProviders( <LoginPage /> );

        await user.type( screen.getByLabelText( /^Username/ ), 'admin' );
        await user.type( screen.getByLabelText( /^Password/ ), 's3cret' );
        await user.click( screen.getByRole( 'button', { name: 'Sign in' } ) );

        await waitFor( () => expect( location.href ).toBe( '/admin/' ) );
        expect( received ).toEqual( { username: 'admin', password: 's3cret' } );
    } );

    it( 'shows an error and stays on the form when the credentials are rejected', async () =>
    {
        stubLocation();
        server.use( http.post( '/api/auth/login', () => new HttpResponse( null, { status: 401 } ) ) );

        const { user } = renderWithProviders( <LoginPage /> );

        await user.type( screen.getByLabelText( /^Username/ ), 'admin' );
        await user.type( screen.getByLabelText( /^Password/ ), 'wrong' );
        await user.click( screen.getByRole( 'button', { name: 'Sign in' } ) );

        expect( await screen.findByText( 'Invalid username or password.' ) ).toBeInTheDocument();
        expect( screen.getByRole( 'button', { name: 'Sign in' } ) ).toBeEnabled();
    } );

    it( 'does not call the API when the required fields are empty', async () =>
    {
        stubLocation();
        const login = vi.fn();
        server.use( http.post( '/api/auth/login', () =>
        {
            login();
            return HttpResponse.json( { ok: true } );
        } ) );

        const { user } = renderWithProviders( <LoginPage /> );
        await user.click( screen.getByRole( 'button', { name: 'Sign in' } ) );

        expect( login ).not.toHaveBeenCalled();
    } );
} );
