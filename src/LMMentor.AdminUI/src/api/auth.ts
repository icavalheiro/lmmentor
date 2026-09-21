// Cliente tipado para os endpoints de autenticação do admin.

export interface AuthUser
{
    username: string;
}

async function request<T> ( path: string, init?: RequestInit ): Promise<T>
{
    const response = await fetch( path, {
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        ...init,
    } );

    if ( !response.ok )
    {
        throw new Error( `Request failed for ${ path }: ${ response.status }` );
    }

    return response.json() as Promise<T>;
}

export function login ( username: string, password: string ): Promise<{ ok: boolean; }>
{
    return request( '/api/auth/login', {
        method: 'POST',
        body: JSON.stringify( { username, password } ),
    } );
}

export function logout (): Promise<{ ok: boolean; }>
{
    return request( '/api/auth/logout', { method: 'POST' } );
}

export function getCurrentUser (): Promise<AuthUser>
{
    return request( '/api/auth/me' );
}
