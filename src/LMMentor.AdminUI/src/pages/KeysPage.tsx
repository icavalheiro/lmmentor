import { Link } from 'react-router-dom';
import { ActionIcon, Badge, Button, Card, Code, Group, Stack, Table, Text, Tooltip } from '@mantine/core';
import { IconBan, IconKey, IconPlus, IconTrash } from '@tabler/icons-react';
import { useDeleteKey, useKeys, useModels, useRevokeKey } from '../api/queries';

function formatDate ( iso: string | null )
{
    if ( !iso )
    {
        return '—';
    }
    return new Date( iso ).toLocaleDateString( 'en-US' );
}

export function KeysPage ()
{
    const { data: keys = [] } = useKeys();
    const { data: models = [] } = useModels();
    const revokeKey = useRevokeKey();
    const deleteKey = useDeleteKey();

    return (
        <Card withBorder padding="lg">
            <Stack gap="md">
                <Group justify="space-between">
                    <div>
                        <Text fw={ 600 } size="lg">API Keys</Text>
                        <Text c="dimmed" size="sm">Keys to access the public LMMentor API.</Text>
                    </div>
                    <Link to="/keys/new">
                        <Button leftSection={ <IconPlus size={ 16 } /> }>Add key</Button>
                    </Link>
                </Group>

                <Table striped highlightOnHover withTableBorder>
                    <Table.Thead>
                        <Table.Tr>
                            <Table.Th>Name</Table.Th>
                            <Table.Th>Key</Table.Th>
                            <Table.Th>Allowed models</Table.Th>
                            <Table.Th>Created</Table.Th>
                            <Table.Th>Status</Table.Th>
                            <Table.Th ta="right">Actions</Table.Th>
                        </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                        { keys.length === 0 && (
                            <Table.Tr>
                                <Table.Td colSpan={ 6 }>
                                    <Text c="dimmed" ta="center">No keys created.</Text>
                                </Table.Td>
                            </Table.Tr>
                        ) }

                        { keys.map( ( key ) =>
                        {
                            const isRevoked = key.revokedAt !== null;
                            return (
                                <Table.Tr key={ key.id } opacity={ isRevoked ? 0.5 : 1 }>
                                    <Table.Td>
                                        <Group gap={ 6 }>
                                            <IconKey size={ 14 } />
                                            <Text fw={ 500 }>{ key.name }</Text>
                                        </Group>
                                    </Table.Td>
                                    <Table.Td>
                                        <Code>{ key.key.slice( 0, 12 ) }…</Code>
                                    </Table.Td>
                                    <Table.Td>
                                        { key.allowedModelIds === null ? (
                                            <Badge variant="light" size="xs">All</Badge>
                                        ) : (
                                            <Group gap={ 4 }>
                                                { key.allowedModelIds.map( ( id ) =>
                                                {
                                                    const model = models.find( ( m ) => m.id === id );
                                                    return (
                                                        <Badge key={ id } variant="outline" size="xs">
                                                            { model?.displayName || model?.upstreamModelId || id }
                                                        </Badge>
                                                    );
                                                } ) }
                                            </Group>
                                        ) }
                                    </Table.Td>
                                    <Table.Td>
                                        <Text size="sm">{ formatDate( key.createdAt ) }</Text>
                                    </Table.Td>
                                    <Table.Td>
                                        { isRevoked ? (
                                            <Badge color="red" variant="light" size="xs">Revoked</Badge>
                                        ) : (
                                            <Badge color="green" variant="light" size="xs">Active</Badge>
                                        ) }
                                    </Table.Td>
                                    <Table.Td ta="right">
                                        <Group gap={ 4 } justify="flex-end">
                                            { !isRevoked && (
                                                <Tooltip label="Revoke">
                                                    <ActionIcon aria-label="Revoke" variant="subtle" color="orange" size="sm" onClick={ () => revokeKey.mutate( key.id ) }>
                                                        <IconBan size={ 16 } />
                                                    </ActionIcon>
                                                </Tooltip>
                                            ) }
                                            <Tooltip label="Delete">
                                                <ActionIcon aria-label="Delete" variant="subtle" color="red" size="sm" onClick={ () => deleteKey.mutate( key.id ) }>
                                                    <IconTrash size={ 16 } />
                                                </ActionIcon>
                                            </Tooltip>
                                        </Group>
                                    </Table.Td>
                                </Table.Tr>
                            );
                        } ) }
                    </Table.Tbody>
                </Table>
            </Stack>
        </Card>
    );
}
