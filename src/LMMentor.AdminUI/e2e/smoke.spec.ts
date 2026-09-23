import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
import { credentialsPath, type E2ECredentials } from './global-setup.ts';

const credentials = JSON.parse( readFileSync( credentialsPath, 'utf8' ) ) as E2ECredentials;

test.describe( 'smoke', () =>
{
    test( 'serves the admin UI build under /admin', async ( { page } ) =>
    {
        await page.goto( '/' );

        // A raiz redireciona para o SPA e o backend serve o index.html do build do Vite.
        await expect( page ).toHaveURL( /\/admin\// );
        await expect( page.getByRole( 'button', { name: 'Sign in' } ) ).toBeVisible();
    } );

    test( 'rejects wrong credentials', async ( { page } ) =>
    {
        await page.goto( '/admin/' );

        await page.getByLabel( /^Username/ ).fill( 'admin' );
        await page.getByLabel( /^Password/ ).fill( 'definitely-wrong' );
        await page.getByRole( 'button', { name: 'Sign in' } ).click();

        await expect( page.getByText( 'Invalid username or password.' ) ).toBeVisible();
    } );

    test( 'signs in and loads the dashboard', async ( { page } ) =>
    {
        await page.goto( '/admin/' );

        await page.getByLabel( /^Username/ ).fill( credentials.username );
        await page.getByLabel( /^Password/ ).fill( credentials.password );
        await page.getByRole( 'button', { name: 'Sign in' } ).click();

        await expect( page.getByRole( 'link', { name: 'API Endpoints' } ) ).toBeVisible();

        // A sessão por cookie mantém o painel acessível nas demais rotas do SPA.
        await page.getByRole( 'link', { name: 'API Keys' } ).click();
        await expect( page.getByText( 'No keys created.' ) ).toBeVisible();
    } );

    test( 'keeps the public relay protected', async ( { request } ) =>
    {
        const health = await request.get( '/api/health' );
        expect( health.ok() ).toBeTruthy();

        const models = await request.get( '/v1/models' );
        expect( models.status() ).toBe( 401 );
    } );
} );
