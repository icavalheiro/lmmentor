import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterAll, afterEach, beforeAll, vi } from 'vitest';
import { server } from './server';

// O jsdom não implementa matchMedia nem ResizeObserver, exigidos pelos componentes do Mantine.
beforeAll( () =>
{
    Object.defineProperty( window, 'matchMedia', {
        writable: true,
        value: ( query: string ) => ( {
            matches: false,
            media: query,
            onchange: null,
            addListener: vi.fn(),
            removeListener: vi.fn(),
            addEventListener: vi.fn(),
            removeEventListener: vi.fn(),
            dispatchEvent: vi.fn(),
        } ),
    } );

    globalThis.ResizeObserver = class
    {
        observe () { }
        unobserve () { }
        disconnect () { }
    };

    window.HTMLElement.prototype.scrollIntoView = vi.fn();

    // Requisição sem handler é erro: nenhum teste pode depender de rede real.
    server.listen( { onUnhandledRequest: 'error' } );
} );

afterEach( () =>
{
    cleanup();
    server.resetHandlers();
} );

afterAll( () => server.close() );
