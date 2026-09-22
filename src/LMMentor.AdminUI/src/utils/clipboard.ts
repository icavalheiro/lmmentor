// Copia texto para a área de transferência com fallback para contextos não seguros
// (ex.: HTTP fora do localhost), onde navigator.clipboard não está disponível.
export async function copyToClipboard ( text: string ): Promise<boolean>
{
    const clipboard = navigator.clipboard;
    if ( clipboard )
    {
        try
        {
            await clipboard.writeText( text );
            return true;
        }
        catch
        {
            // Permissão negada ou falha: tenta o fallback via execCommand.
        }
    }

    const textarea = document.createElement( 'textarea' );
    textarea.value = text;
    textarea.style.position = 'fixed';
    textarea.style.opacity = '0';
    document.body.appendChild( textarea );
    textarea.select();

    let copied = false;
    try
    {
        copied = document.execCommand( 'copy' );
    }
    finally
    {
        document.body.removeChild( textarea );
    }
    return copied;
}
