// Cliente HTTP base para a API do admin. Usa cookies de sessão (same-origin).

export class ApiError extends Error
{
    readonly status: number;

    constructor ( status: number, message: string )
    {
        super( message );
        this.name = 'ApiError';
        this.status = status;
    }
}

export async function request<T> ( path: string, init?: RequestInit ): Promise<T>
{
    const response = await fetch( path, {
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        ...init,
    } );

    if ( !response.ok )
    {
        let message = `Request failed for ${ path }: ${ response.status }`;
        try
        {
            const body = await response.json() as { error?: string; };
            if ( body.error )
            {
                message = body.error;
            }
        }
        catch
        {
            // Corpo não-JSON: mantém a mensagem padrão.
        }
        throw new ApiError( response.status, message );
    }

    if ( response.status === 204 )
    {
        return undefined as T;
    }

    return response.json() as Promise<T>;
}
