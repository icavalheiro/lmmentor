import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ActionIcon, Badge, Button, Card, Code, Group, Stack, Switch, Table, Text, TextInput, Tooltip } from '@mantine/core';
import { IconArrowLeft, IconPencil, IconRefresh } from '@tabler/icons-react';
import { useAdminData } from '../context/AdminDataContext';

// Nome efetivamente exposto: alias customizado ou o nome upstream.
function effectiveName ( displayName: string, upstreamModelId: string )
{
    return displayName.trim() || upstreamModelId;
}

function formatDateTime ( iso: string )
{
    return new Date( iso ).toLocaleString( 'en-US', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' } );
}

// Candidatos fake que podem ser "descobertos" ao forçar a atualização.
const DISCOVERABLE_MODELS: { upstreamModelId: string; contextSize: number | null; }[] = [
    { upstreamModelId: 'qwen2.5-72b-instruct', contextSize: 32768 },
    { upstreamModelId: 'deepseek-r1-distill-qwen-32b', contextSize: 65536 },
    { upstreamModelId: 'mistral-small-3.1-24b', contextSize: 131072 },
];

export function EndpointDetailPage ()
{
    const { id } = useParams<{ id: string; }>();
    const { endpoints, models, setEndpoints, setModels } = useAdminData();

    const endpoint = endpoints.find( ( e ) => e.id === id );
    const endpointModels = models.filter( ( m ) => m.endpointId === id );

    const [ editingId, setEditingId ] = useState<string | null>( null );
    const [ draftName, setDraftName ] = useState( '' );
    const [ refreshing, setRefreshing ] = useState( false );

    if ( !endpoint )
    {
        return (
            <Card withBorder padding="lg">
                <Text c="dimmed">Endpoint not found.</Text>
                <Link to="/endpoints"><Button variant="default" size="xs" mt="sm">Back to endpoints</Button></Link>
            </Card>
        );
    }

    // Nomes já usados por OUTROS modelos (de qualquer endpoint) — base da validação de duplicidade.
    function namesInUseExcept ( exceptModelId: string )
    {
        return new Set(
            models
                .filter( ( m ) => m.id !== exceptModelId )
                .map( ( m ) => effectiveName( m.displayName, m.upstreamModelId ).toLowerCase() ),
        );
    }

    // Exclui o modelo em edição para que manter o nome atual não seja tratado como duplicado.
    const duplicateNames = namesInUseExcept( editingId ?? '' );
    const modelBeingEdited = endpointModels.find( ( m ) => m.id === editingId );
    // O nome efetivo do rascunho: alias digitado ou, se vazio, o nome upstream.
    const draftEffectiveName = modelBeingEdited ? effectiveName( draftName, modelBeingEdited.upstreamModelId ) : '';
    const draftIsDuplicate = editingId !== null && duplicateNames.has( draftEffectiveName.toLowerCase() );

    function startEditing ( modelId: string, currentDisplayName: string )
    {
        setEditingId( modelId );
        setDraftName( currentDisplayName );
    }

    function commitEdit ()
    {
        if ( editingId === null || draftIsDuplicate )
        {
            return;
        }
        setModels( ( prev ) => prev.map( ( m ) => ( m.id === editingId ? { ...m, displayName: draftName.trim() } : m ) ) );
        setEditingId( null );
    }

    function toggleEnabled ( modelId: string, enabled: boolean )
    {
        setModels( ( prev ) => prev.map( ( m ) => ( m.id === modelId ? { ...m, enabled } : m ) ) );
    }

    // Força a re-verificação do status e a descoberta de novos modelos (simulado).
    function handleRefresh ()
    {
        if ( !endpoint || refreshing )
        {
            return;
        }

        setRefreshing( true );

        window.setTimeout( () =>
        {
            const existing = new Set( models.filter( ( m ) => m.endpointId === endpoint.id ).map( ( m ) => m.upstreamModelId ) );
            const candidate = DISCOVERABLE_MODELS.find( ( c ) => !existing.has( c.upstreamModelId ) );

            // Atualiza o status e o horário da última verificação.
            setEndpoints( ( prev ) => prev.map( ( e ) => ( e.id === endpoint.id ? { ...e, status: 'online', lastCheckedAt: new Date().toISOString() } : e ) ) );

            // Adiciona um novo modelo descoberto, se houver candidato ainda não listado.
            if ( candidate )
            {
                setModels( ( prev ) => [ ...prev, {
                    id: `${ endpoint.id }-m${ Date.now() }`,
                    endpointId: endpoint.id,
                    upstreamModelId: candidate.upstreamModelId,
                    displayName: '',
                    contextSize: candidate.contextSize,
                    enabled: true,
                } ] );
            }

            setRefreshing( false );
        }, 1200 );
    }

    return (
        <Card withBorder padding="lg">
            <Stack gap="md">
                <Group>
                    <Link to="/endpoints">
                        <Button variant="subtle" color="dimmed" leftSection={ <IconArrowLeft size={ 14 } /> } size="xs">Back</Button>
                    </Link>
                </Group>

                <Group justify="space-between">
                    <div>
                        <Text fw={ 600 } size="lg">{ endpoint.name }</Text>
                        <Group gap="sm" mt={ 4 }>
                            <Badge variant="outline" color="gray">{ endpoint.type }</Badge>
                            <Text size="sm" c="dimmed">{ endpoint.url }</Text>
                            { endpoint.accessToken && <Code>token: ••••</Code> }
                        </Group>
                        <Text size="xs" c="dimmed">Last checked { formatDateTime( endpoint.lastCheckedAt ) }</Text>
                    </div>
                    <Group gap="sm">
                        <Button
                            variant="light"
                            size="xs"
                            leftSection={ <IconRefresh size={ 14 } /> }
                            loading={ refreshing }
                            onClick={ handleRefresh }
                        >
                            Refresh
                        </Button>
                        { endpoint.status === 'online' ? (
                            <Badge color="green" variant="light" size="xs">Online</Badge>
                        ) : (
                            <Badge color="red" variant="light" size="xs">Offline</Badge>
                        ) }
                    </Group>
                </Group>

                <div>
                    <Text fw={ 600 }>Discovered models</Text>
                    <Text c="dimmed" size="sm">
                        Enable/disable models and customize the exposed name. Names must be unique across all endpoints.
                    </Text>
                </div>

                <Table striped highlightOnHover withTableBorder>
                    <Table.Thead>
                        <Table.Tr>
                            <Table.Th>Upstream model</Table.Th>
                            <Table.Th>Exposed name</Table.Th>
                            <Table.Th ta="right">Context</Table.Th>
                            <Table.Th ta="center">Enabled</Table.Th>
                        </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                        { endpointModels.length === 0 && (
                            <Table.Tr>
                                <Table.Td colSpan={ 4 }>
                                    <Text c="dimmed" ta="center">No models discovered on this endpoint.</Text>
                                </Table.Td>
                            </Table.Tr>
                        ) }

                        { endpointModels.map( ( model ) => (
                            <Table.Tr key={ model.id } opacity={ model.enabled ? 1 : 0.5 }>
                                <Table.Td>
                                    <Text fw={ 500 }>{ model.upstreamModelId }</Text>
                                </Table.Td>
                                <Table.Td>
                                    { editingId === model.id ? (
                                        <Stack gap={ 4 }>
                                            <TextInput
                                                size="xs"
                                                value={ draftName }
                                                placeholder={ model.upstreamModelId }
                                                autoFocus
                                                error={ draftIsDuplicate ? 'A model with this name already exists on another endpoint.' : undefined }
                                                onChange={ ( e ) => setDraftName( e.currentTarget.value ) }
                                                onKeyDown={ ( e ) =>
                                                {
                                                    if ( e.key === 'Enter' )
                                                    {
                                                        commitEdit();
                                                    }
                                                    if ( e.key === 'Escape' )
                                                    {
                                                        setEditingId( null );
                                                    }
                                                } }
                                            />
                                            <Group gap={ 4 }>
                                                <Button size="xs" variant="light" onClick={ commitEdit } disabled={ draftIsDuplicate }>Save</Button>
                                                <Button size="xs" variant="default" onClick={ () => setEditingId( null ) }>Cancel</Button>
                                            </Group>
                                        </Stack>
                                    ) : (
                                        <Group gap={ 6 }>
                                            <Text c={ model.displayName ? 'inherit' : 'dimmed' }>
                                                { effectiveName( model.displayName, model.upstreamModelId ) }
                                            </Text>
                                            <Tooltip label="Rename">
                                                <ActionIcon variant="subtle" size="xs" onClick={ () => startEditing( model.id, model.displayName ) }>
                                                    <IconPencil size={ 14 } />
                                                </ActionIcon>
                                            </Tooltip>
                                        </Group>
                                    ) }
                                </Table.Td>
                                <Table.Td ta="right">
                                    <Text size="sm">{ model.contextSize ? model.contextSize.toLocaleString() : '—' }</Text>
                                </Table.Td>
                                <Table.Td ta="center">
                                    <Switch
                                        size="xs"
                                        checked={ model.enabled }
                                        onChange={ ( e ) => toggleEnabled( model.id, e.currentTarget.checked ) }
                                    />
                                </Table.Td>
                            </Table.Tr>
                        ) ) }
                    </Table.Tbody>
                </Table>
            </Stack>
        </Card>
    );
}
