import { HttpResponse, http } from 'msw';
import { describe, expect, it } from 'vitest';
import { ApiError, request } from './http';
import { server } from '../test/server';

describe( 'request', () =>
{
    it( 'returns the parsed JSON body on success', async () =>
    {
        server.use( http.get( '/api/thing', () => HttpResponse.json( { id: 'abc' } ) ) );

        await expect( request<{ id: string; }>( '/api/thing' ) ).resolves.toEqual( { id: 'abc' } );
    } );

    it( 'sends cookies and the JSON content type', async () =>
    {
        let contentType: string | null = null;
        server.use( http.post( '/api/thing', ( { request: received } ) =>
        {
            contentType = received.headers.get( 'Content-Type' );
            return HttpResponse.json( { ok: true } );
        } ) );

        await request( '/api/thing', { method: 'POST', body: JSON.stringify( { a: 1 } ) } );

        expect( contentType ).toBe( 'application/json' );
    } );

    it( 'resolves with undefined for 204 responses', async () =>
    {
        server.use( http.delete( '/api/thing', () => new HttpResponse( null, { status: 204 } ) ) );

        await expect( request( '/api/thing', { method: 'DELETE' } ) ).resolves.toBeUndefined();
    } );

    it( 'throws an ApiError carrying the server message', async () =>
    {
        server.use( http.post( '/api/thing', () => HttpResponse.json( { error: 'Name is required.' }, { status: 400 } ) ) );

        const failure = request( '/api/thing', { method: 'POST' } );

        await expect( failure ).rejects.toBeInstanceOf( ApiError );
        await expect( failure ).rejects.toMatchObject( { status: 400, message: 'Name is required.' } );
    } );

    it( 'falls back to a generic message when the error body is not JSON', async () =>
    {
        server.use( http.get( '/api/thing', () => new HttpResponse( 'boom', { status: 500 } ) ) );

        await expect( request( '/api/thing' ) ).rejects.toMatchObject( {
            status: 500,
            message: 'Request failed for /api/thing: 500',
        } );
    } );

    it( 'reports the unauthorized status so the app can show the login screen', async () =>
    {
        server.use( http.get( '/api/auth/me', () => new HttpResponse( null, { status: 401 } ) ) );

        await expect( request( '/api/auth/me' ) ).rejects.toMatchObject( { status: 401 } );
    } );
} );
