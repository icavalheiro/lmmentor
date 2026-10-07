import { useCallback, useRef, useState } from 'react';
import { Alert, Button, Group, Loader, Modal, Stack, Text, Textarea } from '@mantine/core';
import { IconDownload } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { getAllModels } from '../api/endpoints';
import { queryKeys } from '../api/queries';
import { copyToClipboard } from '../utils/clipboard';
import { exportSchema, getExportModels, HARNESSES, type Harness } from '../utils/schemaExport';

export function ExportSchemaButton ()
{
    const [ opened, setOpened ] = useState( false );
    const [ harness, setHarness ] = useState<Harness | null>( null );
    const [ copyStatus, setCopyStatus ] = useState( '' );
    const textarea = useRef<HTMLTextAreaElement>( null );
    const attachTextarea = useCallback( ( node: HTMLTextAreaElement | null ) =>
    {
        textarea.current = node;
        if ( node ) requestAnimationFrame( () => { if ( node.isConnected ) { node.focus(); node.select(); } } );
    }, [] );
    const { data: models = [], isFetching, isError, refetch } = useQuery( {
        queryKey: queryKeys.models, queryFn: getAllModels, enabled: opened,
    } );
    const catalog = getExportModels( models );
    const selectedHarness = HARNESSES.find( ( item ) => item.value === harness );
    const json = harness ? exportSchema( harness, models, window.location.origin ) : '';
    const missingLimits = catalog.some( ( model ) => model.context === undefined || model.output === undefined );
    const ready = !isFetching && !isError && catalog.length > 0;

    function selectText ()
    {
        textarea.current?.focus();
        textarea.current?.select();
    }

    async function handleCopy ()
    {
        try
        {
            const copied = await copyToClipboard( json );
            setCopyStatus( copied ? 'JSON copiado.' : 'Não foi possível copiar. Use Ctrl+C no texto selecionado.' );
        }
        catch
        {
            setCopyStatus( 'Não foi possível copiar. Use Ctrl+C no texto selecionado.' );
        }
        selectText();
    }

    return (
        <>
            <Button variant="default" size="xs" leftSection={ <IconDownload size={ 14 } /> } onClick={ () =>
            {
                setHarness( null );
                setCopyStatus( '' );
                setOpened( true );
            } }>Exporta schema</Button>
            <Modal opened={ opened } onClose={ () => setOpened( false ) }
                title={ selectedHarness ? `Schema JSON — ${ selectedHarness.label }` : 'Para qual harness deseja exportar?' }
                size={ harness ? 'xl' : 'sm' } onTransitionEnd={ () => { if ( harness && ready ) selectText(); } }>
                <Stack>
                    { !harness ? (
                        <Stack gap="xs">
                            { HARNESSES.map( ( item ) => (
                                <Button key={ item.value } variant="light" onClick={ () => setHarness( item.value ) }>{ item.label }</Button>
                            ) ) }
                        </Stack>
                    ) : (
                        <>
                            <Text size="sm">{ selectedHarness?.destination } · { catalog.length } modelos habilitados</Text>
                            <Text size="sm" c="dimmed">Use uma chave criada em API Keys. A exportação aponta para { window.location.origin }.</Text>
                            { harness === 'claude' && <Alert color="blue">Cada entrada em profiles contém um settings.json independente. Salve o objeto settings do modelo desejado e inicie Claude com --settings caminho/do/settings.json. Os limites valem para essa sessão.</Alert> }
                            { harness === 'deepseek' && <Text size="sm" c="dimmed">Mescle a entrada llm-pi-ai no patch do perfil ativo, preservando os outros providers.</Text> }
                            { isFetching && <Group><Loader size="sm" /><Text size="sm">Carregando modelos…</Text></Group> }
                            { isError && <Alert color="red" title="Não foi possível carregar os modelos"><Button variant="light" onClick={ () => void refetch() }>Tentar novamente</Button></Alert> }
                            { !isFetching && !isError && catalog.length === 0 && <Alert color="blue">Nenhum modelo habilitado. Habilite modelos em API Endpoints para exportar.</Alert> }
                            { ready && <>
                                { missingLimits && <Alert color="yellow">Alguns modelos não informam contexto ou output. Esses campos foram omitidos; o harness poderá aplicar seus próprios valores padrão.</Alert> }
                                <Textarea key={ harness } ref={ attachTextarea } label="Schema JSON" value={ json } readOnly
                                    data-autofocus onFocus={ ( event ) => event.currentTarget.select() }
                                    rows={ 18 } styles={ { input: { fontFamily: 'monospace', fontSize: 13 } } } />
                            </> }
                            <Group justify="space-between">
                                <Button variant="default" onClick={ () => { setHarness( null ); setCopyStatus( '' ); } }>Trocar harness</Button>
                                <Button disabled={ !ready } onClick={ handleCopy }>Copiar JSON</Button>
                            </Group>
                            { copyStatus && <Text size="sm" role="status">{ copyStatus }</Text> }
                        </>
                    ) }
                </Stack>
            </Modal>
        </>
    );
}
