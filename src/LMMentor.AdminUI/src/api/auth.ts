import { request } from './http';

// Cliente tipado para os endpoints de autenticação do admin.

export interface AuthUser
{
    username: string;
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
