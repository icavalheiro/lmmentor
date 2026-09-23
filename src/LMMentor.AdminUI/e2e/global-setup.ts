import { spawn, type ChildProcess } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';

const currentDir = dirname( fileURLToPath( import.meta.url ) );
const backendProject = resolve( currentDir, '../../LMMentor.Backend' );

export const credentialsPath = resolve( currentDir, '.auth/credentials.json' );

export interface E2ECredentials
{
    baseURL: string;
    username: string;
    password: string;
}

/**
 * Sobe o backend real com um banco temporário e captura as credenciais de admin
 * geradas no primeiro startup (impressas no console pelo bootstrap).
 */
export default async function globalSetup ()
{
    const port = Number( process.env.LMMENTOR_E2E_PORT ?? 5199 );
    const baseURL = `http://127.0.0.1:${ port }`;
    const databasePath = resolve( tmpdir(), `lmmentor-e2e-${ Date.now() }.db` );

    const backend = spawn(
        'dotnet',
        [ 'run', '--project', backendProject, '-c', 'Release', '--no-launch-profile' ],
        {
            cwd: backendProject,
            env: {
                ...process.env,
                ASPNETCORE_ENVIRONMENT: 'Production',
                ASPNETCORE_URLS: baseURL.replace( '127.0.0.1', '+' ),
                LMMENTOR_DB_PATH: databasePath,
            },
        },
    );

    const password = await waitForGeneratedPassword( backend );
    await waitForHealth( baseURL );

    mkdirSync( dirname( credentialsPath ), { recursive: true } );
    const credentials: E2ECredentials = { baseURL, username: 'admin', password };
    writeFileSync( credentialsPath, JSON.stringify( credentials ), 'utf8' );

    return async () =>
    {
        await stopProcessTree( backend );
        rmSync( databasePath, { force: true } );
        rmSync( `${ databasePath }-log`, { force: true } );
    };
}

/** Lê o stdout até o bootstrap imprimir a senha do primeiro startup. */
function waitForGeneratedPassword ( backend: ChildProcess )
{
    return new Promise<string>( ( resolvePassword, reject ) =>
    {
        let output = '';
        const timeout = setTimeout( () => reject( new Error( `Backend did not print admin credentials.\n${ output }` ) ), 180_000 );

        backend.stdout?.on( 'data', ( chunk: Buffer ) =>
        {
            output += chunk.toString();
            const match = /Senha:\s*(\S+)/.exec( output );
            if ( !match )
            {
                return;
            }

            clearTimeout( timeout );
            resolvePassword( match[ 1 ] );
        } );

        backend.stderr?.on( 'data', ( chunk: Buffer ) => { output += chunk.toString(); } );
        backend.on( 'exit', ( code ) =>
        {
            clearTimeout( timeout );
            reject( new Error( `Backend exited with code ${ code }.\n${ output }` ) );
        } );
    } );
}

async function waitForHealth ( baseURL: string )
{
    for ( let attempt = 0; attempt < 60; attempt++ )
    {
        try
        {
            const response = await fetch( `${ baseURL }/api/health` );
            if ( response.ok )
            {
                return;
            }
        }
        catch
        {
            // Servidor ainda subindo: tenta de novo.
        }

        await new Promise( ( done ) => setTimeout( done, 500 ) );
    }

    throw new Error( `Backend did not become healthy at ${ baseURL }.` );
}

/** No Windows o `dotnet run` cria um processo filho: matar só o pai deixaria o servidor vivo. */
function stopProcessTree ( backend: ChildProcess )
{
    return new Promise<void>( ( done ) =>
    {
        if ( backend.exitCode !== null || backend.pid === undefined )
        {
            done();
            return;
        }

        backend.once( 'exit', () => done() );

        if ( process.platform === 'win32' )
        {
            spawn( 'taskkill', [ '/pid', String( backend.pid ), '/t', '/f' ] );
            return;
        }

        backend.kill( 'SIGTERM' );
    } );
}
