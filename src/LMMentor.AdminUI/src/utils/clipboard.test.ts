import { describe, expect, it, vi } from 'vitest';
import { copyToClipboard } from './clipboard';

function stubClipboard ( writeText: () => Promise<void> )
{
    Object.defineProperty( navigator, 'clipboard', {
        configurable: true,
        value: { writeText },
    } );
}

function removeClipboard ()
{
    Object.defineProperty( navigator, 'clipboard', { configurable: true, value: undefined } );
}

describe( 'copyToClipboard', () =>
{
    it( 'uses the Clipboard API when it is available', async () =>
    {
        const writeText = vi.fn().mockResolvedValue( undefined );
        stubClipboard( writeText );

        await expect( copyToClipboard( 'sk-lm-abc' ) ).resolves.toBe( true );
        expect( writeText ).toHaveBeenCalledWith( 'sk-lm-abc' );
    } );

    it( 'falls back to execCommand when the Clipboard API is missing', async () =>
    {
        removeClipboard();
        const execCommand = vi.fn().mockReturnValue( true );
        document.execCommand = execCommand;

        await expect( copyToClipboard( 'sk-lm-abc' ) ).resolves.toBe( true );
        expect( execCommand ).toHaveBeenCalledWith( 'copy' );
        // O textarea temporário não pode sobrar no documento.
        expect( document.querySelector( 'textarea' ) ).toBeNull();
    } );

    it( 'falls back to execCommand when the Clipboard API is denied', async () =>
    {
        stubClipboard( vi.fn().mockRejectedValue( new Error( 'denied' ) ) );
        const execCommand = vi.fn().mockReturnValue( false );
        document.execCommand = execCommand;

        await expect( copyToClipboard( 'sk-lm-abc' ) ).resolves.toBe( false );
        expect( execCommand ).toHaveBeenCalledWith( 'copy' );
    } );
} );
