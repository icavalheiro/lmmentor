import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ActionIcon, Badge, Button, Card, Chip, Code, Group, Modal, Stack, Switch, Table, Text, TextInput, Tooltip } from '@mantine/core';
import { IconArrowLeft, IconClock, IconPencil, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react';
import { useEndpoints, useModels, useRefreshEndpoint, useRenameModel, useSetModelSchedule, useToggleModelEnabled } from '../api/queries';
import type { AvailabilityWindow } from '../types';

const DAY_LABELS = [ 'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat' ];

// Nome efetivamente exposto: alias customizado ou o nome upstream.
function effectiveName ( displayName: string, upstreamModelId: string )
{
    return displayName.trim() || upstreamModelId;
}

function formatDateTime ( iso: string )
{
    return new Date( iso ).toLocaleString( 'en-US', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' } );
}

function formatSchedule ( windows: AvailabilityWindow[] )
{
    if ( windows.length === 0 )
    {
        return 'Always available';
    }
    return `${ windows.length } restriction${ windows.length > 1 ? 's' : '' }`;
}

export function EndpointDetailPage ()
{
    const { id } = useParams<{ id: string; }>();
    const { data: endpoints = [] } = useEndpoints();
    const { data: models = [] } = useModels();
    const refreshEndpoint = useRefreshEndpoint();
    const renameModel = useRenameModel();
    const toggleModelEnabled = useToggleModelEnabled();
    const setModelSchedule = useSetModelSchedule();

    const endpoint = endpoints.find( ( e ) => e.id === id );
    const endpointModels = models.filter( ( m ) => m.endpointId === id );

    const [ editingId, setEditingId ] = useState<string | null>( null );
    const [ draftName, setDraftName ] = useState( '' );
    const [ draftError, setDraftError ] = useState( '' );

    const [ scheduleModelId, setScheduleModelId ] = useState<string | null>( null );
    const [ draftWindows, setDraftWindows ] = useState<AvailabilityWindow[]>( [] );

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
        setDraftError( '' );
    }

    function commitEdit ()
    {
        if ( editingId === null || draftIsDuplicate || renameModel.isPending )
        {
            return;
        }
        renameModel.mutate(
            { modelId: editingId, displayName: draftName.trim() },
            {
                onSuccess: () =>
                {
                    setEditingId( null );
                    setDraftError( '' );
                },
                onError: ( err ) => setDraftError( err instanceof Error ? err.message : 'Failed to save the name.' ),
            },
        );
    }

    function toggleEnabled ( modelId: string, enabled: boolean )
    {
        toggleModelEnabled.mutate( { modelId, enabled } );
    }

    // Abre o editor com uma cópia das janelas atuais do modelo (rascunho editável).
    function openScheduleEditor ( modelId: string, blockedWindows: AvailabilityWindow[] )
    {
        setScheduleModelId( modelId );
        setDraftWindows( blockedWindows.map( ( w ) => ( { ...w, daysOfWeek: [ ...w.daysOfWeek ] } ) ) );
    }

    function closeScheduleEditor ()
    {
        setScheduleModelId( null );
    }

    function addWindow ()
    {
        setDraftWindows( [ ...draftWindows, { daysOfWeek: [], startTime: '00:00', endTime: '00:00' } ] );
    }

    function removeWindow ( index: number )
    {
        setDraftWindows( draftWindows.filter( ( _, i ) => i !== index ) );
    }

    function updateWindow ( index: number, patch: Partial<AvailabilityWindow> )
    {
        setDraftWindows( draftWindows.map( ( w, i ) => ( i === index ? { ...w, ...patch } : w ) ) );
    }

    function toggleWindowDays ( index: number, values: string[] )
    {
        updateWindow( index, { daysOfWeek: values.map( Number ) } );
    }

    // Toda janela precisa de ao menos um dia selecionado para ser salva.
    const scheduleIsValid = draftWindows.every( ( w ) => w.daysOfWeek.length > 0 );

    function saveSchedule ()
    {
        if ( scheduleModelId === null || !scheduleIsValid || setModelSchedule.isPending )
        {
            return;
        }
        setModelSchedule.mutate(
            { modelId: scheduleModelId, blockedWindows: draftWindows },
            { onSuccess: closeScheduleEditor },
        );
    }

    // Força a re-verificação do status e a descoberta de novos modelos no provedor.
    function handleRefresh ()
    {
        if ( !endpoint )
        {
            return;
        }
        refreshEndpoint.mutate( endpoint.id );
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
                            loading={ refreshEndpoint.isPending }
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
                        Models are discovered disabled — enable only the ones you want to expose and customize the exposed name.
                        Names must be unique across all endpoints.
                    </Text>
                </div>

                <Table striped highlightOnHover withTableBorder>
                    <Table.Thead>
                        <Table.Tr>
                            <Table.Th>Upstream model</Table.Th>
                            <Table.Th>Exposed name</Table.Th>
                            <Table.Th ta="right">Context</Table.Th>
                            <Table.Th ta="center">Enabled</Table.Th>
                            <Table.Th ta="center">Schedule</Table.Th>
                        </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                        { endpointModels.length === 0 && (
                            <Table.Tr>
                                <Table.Td colSpan={ 5 }>
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
                                                error={ draftIsDuplicate ? 'A model with this name already exists on another endpoint.' : draftError || undefined }
                                                onChange={ ( e ) => { setDraftName( e.currentTarget.value ); setDraftError( '' ); } }
                                                onKeyDown={ ( e ) =>
                                                {
                                                    if ( e.key === 'Enter' )
                                                    {
                                                        commitEdit();
                                                    }
                                                    if ( e.key === 'Escape' )
                                                    {
                                                        setEditingId( null );
                                                        setDraftError( '' );
                                                    }
                                                } }
                                            />
                                            <Group gap={ 4 }>
                                                <Button size="xs" variant="light" loading={ renameModel.isPending } onClick={ commitEdit } disabled={ draftIsDuplicate || renameModel.isPending }>Save</Button>
                                                <Button size="xs" variant="default" onClick={ () => { setEditingId( null ); setDraftError( '' ); } }>Cancel</Button>
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
                                <Table.Td ta="center">
                                    <Button
                                        size="xs"
                                        variant="light"
                                        color={ model.blockedWindows.length > 0 ? 'orange' : 'gray' }
                                        leftSection={ <IconClock size={ 14 } /> }
                                        onClick={ () => openScheduleEditor( model.id, model.blockedWindows ) }
                                    >
                                        { formatSchedule( model.blockedWindows ) }
                                    </Button>
                                </Table.Td>
                            </Table.Tr>
                        ) ) }
                    </Table.Tbody>
                </Table>
            </Stack>

            {/* Editor de janelas recorrentes de indisponibilidade (ex.: rush hour de um provedor) */ }
            <Modal opened={ scheduleModelId !== null } onClose={ closeScheduleEditor } title="Availability schedule" centered size="lg">
                <Stack gap="md">
                    <Text size="sm" c="dimmed">
                        Block the model during recurring days/hours (server local time). Requests to this model during a
                        blocked window get a 503 Service Unavailable response instead of being relayed upstream.
                    </Text>

                    { draftWindows.length === 0 && (
                        <Text c="dimmed" size="sm" ta="center">No restrictions configured.</Text>
                    ) }

                    { draftWindows.map( ( entry, index ) => (
                        <Stack key={ index } gap={ 6 } p="sm" style={ { border: '1px solid var(--mantine-color-gray-3)', borderRadius: 8 } }>
                            <Group justify="space-between" wrap="nowrap">
                                <Chip.Group
                                    multiple
                                    value={ entry.daysOfWeek.map( String ) }
                                    onChange={ ( values ) => toggleWindowDays( index, values ) }
                                >
                                    <Group gap={ 4 }>
                                        { DAY_LABELS.map( ( label, day ) => (
                                            <Chip key={ day } value={ String( day ) } size="xs">{ label }</Chip>
                                        ) ) }
                                    </Group>
                                </Chip.Group>
                                <ActionIcon variant="subtle" color="red" onClick={ () => removeWindow( index ) }>
                                    <IconTrash size={ 14 } />
                                </ActionIcon>
                            </Group>
                            <Group gap="sm">
                                <TextInput
                                    type="time"
                                    label="From"
                                    size="xs"
                                    value={ entry.startTime }
                                    onChange={ ( e ) => updateWindow( index, { startTime: e.currentTarget.value } ) }
                                />
                                <TextInput
                                    type="time"
                                    label="To"
                                    size="xs"
                                    value={ entry.endTime }
                                    onChange={ ( e ) => updateWindow( index, { endTime: e.currentTarget.value } ) }
                                />
                            </Group>
                            { entry.daysOfWeek.length === 0 && (
                                <Text c="red" size="xs">Select at least one day.</Text>
                            ) }
                        </Stack>
                    ) ) }

                    <Button variant="default" size="xs" leftSection={ <IconPlus size={ 14 } /> } onClick={ addWindow }>
                        Add window
                    </Button>

                    <Group justify="flex-end" mt="sm">
                        <Button variant="default" onClick={ closeScheduleEditor }>Cancel</Button>
                        <Button
                            loading={ setModelSchedule.isPending }
                            disabled={ !scheduleIsValid || setModelSchedule.isPending }
                            onClick={ saveSchedule }
                        >
                            Save
                        </Button>
                    </Group>
                </Stack>
            </Modal>
        </Card>
    );
}
