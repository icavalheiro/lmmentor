import type { ReactElement, ReactNode } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

// Sem retry: um erro da API deve aparecer imediatamente no teste, sem espera.
function createTestQueryClient ()
{
    return new QueryClient( {
        defaultOptions: {
            queries: { retry: false, gcTime: 0 },
            mutations: { retry: false },
        },
    } );
}

interface RenderOptions
{
    /** Rota inicial do MemoryRouter (páginas que usam Link/useNavigate). */
    route?: string;
}

/** Renderiza com os providers reais da aplicação (Mantine, TanStack Query e rotas). */
export function renderWithProviders ( ui: ReactElement, { route = '/' }: RenderOptions = {} )
{
    const queryClient = createTestQueryClient();

    function Wrapper ( { children }: { children: ReactNode; } )
    {
        return (
            <MantineProvider>
                <QueryClientProvider client={ queryClient }>
                    <MemoryRouter initialEntries={ [ route ] }>
                        { children }
                    </MemoryRouter>
                </QueryClientProvider>
            </MantineProvider>
        );
    }

    return {
        user: userEvent.setup(),
        queryClient,
        ...render( ui, { wrapper: Wrapper } ),
    };
}
